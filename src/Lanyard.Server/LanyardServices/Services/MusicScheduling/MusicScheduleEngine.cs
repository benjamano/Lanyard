using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services.MusicScheduling;

/// <summary>
/// Puts each kiosk on its location's current part of the day.
/// </summary>
/// <remarks>
/// State-based rather than fired once at a set time, unlike scheduled automation rules: every
/// tick works out what should be playing now and compares it with what was last applied to each
/// kiosk. A restart, a kiosk reconnecting or a missed tick therefore just applies the current part
/// of the day at the next look, and nothing is applied twice within one part of the day.
///
/// A change by hand (any music command not from this engine) holds the schedule off that kiosk
/// until its next part of the day starts. The hold is stored on the Client row, so it survives a
/// restart.
/// </remarks>
public class MusicScheduleEngine : IMusicScheduleEngine
{
    // A playlist change waits for the song playing to end, but a long mix shouldn't hold the old
    // playlist for most of the next part of the day.
    public static readonly TimeSpan SwitchAfterAtMost = TimeSpan.FromMinutes(8);

    private const int RampSteps = 5;
    private const string Gap = "gap";

    private readonly ISystemDbContextFactory _contextFactory;
    private readonly IMusicPlayerService _player;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MusicScheduleEngine> _logger;

    // Kiosk -> the part of the day last applied to it (see OccurrenceKey). A kiosk that drops off a
    // schedule, or disconnects, is forgotten so it's applied afresh when it comes back.
    private readonly ConcurrentDictionary<Guid, string> _applied = new();

    // Kiosk -> when this engine queued an end-of-song playlist switch on it.
    private readonly ConcurrentDictionary<Guid, DateTime> _pendingSince = new();

    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public MusicScheduleEngine(
        ISystemDbContextFactory contextFactory,
        IMusicPlayerService player,
        TimeProvider timeProvider,
        ILogger<MusicScheduleEngine> logger)
    {
        _contextFactory = contextFactory;
        _player = player;
        _timeProvider = timeProvider;
        _logger = logger;

        _player.OnManualMusicChange += HandleManualMusicChange;
    }

    public event Action<Guid>? OnStatusChanged;

    // The gap between volume steps; a full ramp takes four of these.
    public TimeSpan RampStepDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task TickAsync(DateTime utcNow, CancellationToken ct)
    {
        await _tickLock.WaitAsync(ct);

        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(ct);

            List<MusicSchedule> schedules = await ctx.MusicSchedules
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsEnabled && ctx.Locations.Any(l => l.Id == x.LocationId && l.IsActive))
                .Include(x => x.Dayparts.Where(d => d.IsActive))
                    .ThenInclude(d => d.Playlist)
                .ToListAsync(ct);

            List<int> locationIds = schedules.Select(x => x.LocationId).ToList();

            var kiosks = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId != null && locationIds.Contains(x.LocationId.Value))
                .Select(x => new { x.Id, LocationId = x.LocationId!.Value, x.MostRecentConnectionId, x.MusicScheduleOverrideUntilUtc })
                .ToListAsync(ct);

            DateTime venueLocal = RotaTime.ToVenueLocal(utcNow);
            HashSet<Guid> scheduled = [];
            List<Task> ramps = [];

            foreach (MusicSchedule schedule in schedules)
            {
                ActiveDaypart? active = MusicDaypartRules.Resolve(schedule.Dayparts, venueLocal);
                string key = active is null ? Gap : OccurrenceKey(active);

                foreach (var kiosk in kiosks.Where(x => x.LocationId == schedule.LocationId))
                {
                    if (!IsConnected(kiosk.MostRecentConnectionId))
                    {
                        continue;
                    }

                    scheduled.Add(kiosk.Id);

                    // Changed by hand: leave it be. What was applied before stays recorded, so the
                    // next part of the day (a new key) is applied as soon as the hold ends.
                    if (kiosk.MusicScheduleOverrideUntilUtc > utcNow)
                    {
                        continue;
                    }

                    try
                    {
                        await SwitchIfLeftWaitingTooLongAsync(kiosk.Id, utcNow);

                        if (_applied.TryGetValue(kiosk.Id, out string? applied) && applied == key)
                        {
                            continue;
                        }

                        _applied[kiosk.Id] = key;

                        if (active is null)
                        {
                            _logger.LogInformation("Music schedule: nothing scheduled now at location {LocationId}, leaving kiosk {ClientId} as it is", schedule.LocationId, kiosk.Id);
                        }
                        else if (await ApplyAsync(kiosk.Id, active.Daypart, utcNow, ct) is Task ramp)
                        {
                            ramps.Add(ramp);
                        }

                        OnStatusChanged?.Invoke(kiosk.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One kiosk failing mustn't stop the rest; forgetting it retries next tick.
                        _applied.TryRemove(kiosk.Id, out _);
                        _logger.LogError(ex, "Music schedule: failed to apply to kiosk {ClientId}", kiosk.Id);
                    }
                }
            }

            ForgetUnscheduled(scheduled);

            await Task.WhenAll(ramps);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    public async Task<Result<bool>> RecordManualChangeAsync(Guid clientId)
    {
        try
        {
            DateTime utcNow = UtcNow;

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync();

            Client? client = await ctx.Clients.FirstOrDefaultAsync(x => x.Id == clientId);
            MusicSchedule? schedule = client is null ? null : await LoadEnabledScheduleAsync(ctx, client.LocationId);

            if (client is null || schedule is null)
            {
                return Result<bool>.Ok(false);
            }

            DateTime? nextLocal = MusicDaypartRules.NextBoundary(schedule.Dayparts, RotaTime.ToVenueLocal(utcNow));
            if (nextLocal is not DateTime next)
            {
                return Result<bool>.Ok(false);
            }

            client.MusicScheduleOverrideUntilUtc = ToUtc(next);
            await ctx.SaveChangesAsync();

            // Whatever was queued for the end of the song belonged to the schedule.
            if (_pendingSince.TryRemove(clientId, out _))
            {
                _player.SetPendingPlaylist(clientId, null);
            }

            _logger.LogInformation("Music schedule: kiosk {ClientId} changed by hand, schedule resumes at {ResumesAtUtc}", clientId, client.MusicScheduleOverrideUntilUtc);

            OnStatusChanged?.Invoke(clientId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Music schedule: failed to record a change by hand on kiosk {ClientId}", clientId);
            return Result<bool>.Fail($"Failed to record the change: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ResumeScheduleAsync(Guid clientId)
    {
        try
        {
            await using (ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync())
            {
                Client? client = await ctx.Clients.FirstOrDefaultAsync(x => x.Id == clientId);

                if (client is null)
                {
                    return Result<bool>.Fail("Kiosk not found.");
                }

                client.MusicScheduleOverrideUntilUtc = null;
                await ctx.SaveChangesAsync();
            }

            // Forgetting what was applied makes the tick below apply the current part of the day again.
            _applied.TryRemove(clientId, out _);

            _logger.LogInformation("Music schedule: resumed on kiosk {ClientId}", clientId);

            await TickAsync(UtcNow, CancellationToken.None);

            OnStatusChanged?.Invoke(clientId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Music schedule: failed to resume on kiosk {ClientId}", clientId);
            return Result<bool>.Fail($"Failed to resume the schedule: {ex.Message}");
        }
    }

    public async Task<Result<MusicScheduleKioskStatus?>> GetStatusAsync(Guid clientId)
    {
        try
        {
            DateTime utcNow = UtcNow;

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync();

            var client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => new { x.LocationId, x.MusicScheduleOverrideUntilUtc })
                .FirstOrDefaultAsync();

            MusicSchedule? schedule = client is null ? null : await LoadEnabledScheduleAsync(ctx, client.LocationId);

            if (client is null || schedule is null)
            {
                return Result<MusicScheduleKioskStatus?>.Ok(null);
            }

            DateTime venueLocal = RotaTime.ToVenueLocal(utcNow);
            ActiveDaypart? active = MusicDaypartRules.Resolve(schedule.Dayparts, venueLocal);
            DateTime? next = MusicDaypartRules.NextBoundary(schedule.Dayparts, venueLocal);

            MusicScheduleKioskStatus status = new(
                active?.Daypart.Name,
                active is not null && active.Daypart.PlaylistId is null,
                next is DateTime n ? ToUtc(n) : null,
                client.MusicScheduleOverrideUntilUtc > utcNow ? client.MusicScheduleOverrideUntilUtc : null);

            return Result<MusicScheduleKioskStatus?>.Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Music schedule: failed to read the status of kiosk {ClientId}", clientId);
            return Result<MusicScheduleKioskStatus?>.Fail($"Failed to read the music schedule: {ex.Message}");
        }
    }

    // SignalRControlHub.ConnectedIds is process-static, so tests override this, the same seam
    // ClientService.GetConnectedConnectionIds gives its own tests.
    protected internal virtual bool IsConnected(string? connectionId) =>
        !string.IsNullOrEmpty(connectionId) && SignalRControlHub.ConnectedIds.Contains(connectionId);

    // The player raises this synchronously from inside the command, so the database write runs
    // separately; RecordManualChangeAsync logs its own failures.
    private void HandleManualMusicChange(Guid clientId)
    {
        _ = RecordManualChangeAsync(clientId);
    }

    // Includes the daypart's last edit, so changing the running part of the day applies straight
    // away instead of waiting for it to come round again.
    private static string OccurrenceKey(ActiveDaypart active) =>
        $"{active.Daypart.Id:N}:{active.StartDate:yyyyMMdd}:{active.Daypart.UpdateDate.Ticks}";

    private static DateTime ToUtc(DateTime venueLocal) =>
        RotaTime.ToUtc(DateOnly.FromDateTime(venueLocal), TimeOnly.FromDateTime(venueLocal));

    private static async Task<MusicSchedule?> LoadEnabledScheduleAsync(ApplicationDbContext ctx, int? locationId)
    {
        if (locationId is not int id)
        {
            return null;
        }

        return await ctx.MusicSchedules
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Dayparts.Where(d => d.IsActive))
            .FirstOrDefaultAsync(x => x.LocationId == id && x.IsEnabled && ctx.Locations.Any(l => l.Id == id && l.IsActive));
    }

    private async Task<Task?> ApplyAsync(Guid clientId, MusicDaypart daypart, DateTime utcNow, CancellationToken ct)
    {
        _pendingSince.TryRemove(clientId, out _);
        _player.SetPendingPlaylist(clientId, null);

        PlaybackState state = _player.GetCurrentPlaybackState(clientId);

        if (daypart.PlaylistId is not Guid playlistId)
        {
            _logger.LogInformation("Music schedule: \"{Daypart}\" starting on kiosk {ClientId}, music off", daypart.Name, clientId);

            if (state == PlaybackState.Playing)
            {
                await _player.Pause(clientId, MusicCommandSource.Schedule);
            }

            return null;
        }

        if (daypart.Playlist is null || daypart.Playlist.DeleteDate is not null)
        {
            _logger.LogWarning("Music schedule: \"{Daypart}\" plays playlist {PlaylistId}, which has been deleted; leaving kiosk {ClientId} as it is", daypart.Name, playlistId, clientId);
            return null;
        }

        bool alreadyPlayingIt = state == PlaybackState.Playing && _player.GetCurrentPlaylist(clientId)?.Id == playlistId;

        if (alreadyPlayingIt)
        {
            // Typically a server restart mid-daypart: reloading would restart the song for nothing.
            _logger.LogInformation("Music schedule: \"{Daypart}\" already playing on kiosk {ClientId}", daypart.Name, clientId);
        }
        else if (state == PlaybackState.Playing)
        {
            _logger.LogInformation("Music schedule: \"{Daypart}\" starting on kiosk {ClientId}, playlist {PlaylistId} queued for the end of the song", daypart.Name, clientId, playlistId);

            _player.SetPendingPlaylist(clientId, playlistId);
            _pendingSince[clientId] = utcNow;
        }
        else
        {
            _logger.LogInformation("Music schedule: \"{Daypart}\" starting on kiosk {ClientId}, playing playlist {PlaylistId}", daypart.Name, clientId, playlistId);

            Result<bool> played = await _player.Play(clientId, playlistId, MusicCommandSource.Schedule);
            if (!played.IsSuccess)
            {
                _logger.LogWarning("Music schedule: couldn't play \"{Daypart}\" on kiosk {ClientId}: {Error}", daypart.Name, clientId, played.Error);
            }
        }

        return daypart.Volume is int volume ? RampVolumeAsync(clientId, volume, ct) : null;
    }

    private async Task SwitchIfLeftWaitingTooLongAsync(Guid clientId, DateTime utcNow)
    {
        if (!_pendingSince.TryGetValue(clientId, out DateTime since))
        {
            return;
        }

        // Gone means the song ended and the player made the switch.
        if (_player.GetPendingPlaylist(clientId) is not { } pending)
        {
            _pendingSince.TryRemove(clientId, out _);
            return;
        }

        if (utcNow - since < SwitchAfterAtMost)
        {
            return;
        }

        _pendingSince.TryRemove(clientId, out _);

        _logger.LogInformation("Music schedule: song on kiosk {ClientId} still playing after {Minutes} minutes, switching to playlist {PlaylistId} now", clientId, SwitchAfterAtMost.TotalMinutes, pending.PlaylistId);

        Result<bool> played = await _player.Play(clientId, pending.PlaylistId, MusicCommandSource.Schedule);
        if (!played.IsSuccess)
        {
            _logger.LogWarning("Music schedule: couldn't switch kiosk {ClientId} to playlist {PlaylistId}: {Error}", clientId, pending.PlaylistId, played.Error);
        }
    }

    // A few steps a second or so apart rather than one jump, so a change of volume isn't jarring.
    private async Task RampVolumeAsync(Guid clientId, int target, CancellationToken ct)
    {
        int from = _player.GetCurrentVolume(clientId);

        if (from == target)
        {
            return;
        }

        for (int step = 1; step <= RampSteps; step++)
        {
            await _player.SetVolume(clientId, from + (target - from) * step / RampSteps, MusicCommandSource.Schedule);

            if (step < RampSteps && RampStepDelay > TimeSpan.Zero)
            {
                await Task.Delay(RampStepDelay, ct);
            }
        }
    }

    private void ForgetUnscheduled(HashSet<Guid> scheduled)
    {
        foreach (Guid clientId in _applied.Keys.Where(x => !scheduled.Contains(x)).ToList())
        {
            _applied.TryRemove(clientId, out _);

            // A switch this engine queued shouldn't fire after the schedule is turned off.
            if (_pendingSince.TryRemove(clientId, out _))
            {
                _player.SetPendingPlaylist(clientId, null);
            }
        }
    }
}
