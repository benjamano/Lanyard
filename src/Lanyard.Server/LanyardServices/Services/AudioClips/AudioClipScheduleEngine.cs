using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.AudioClips;

public interface IAudioClipScheduleEngine
{
    Task TickAsync(DateTime utcNow, CancellationToken ct);
}

/// <summary>
/// Plays each audio clip schedule when it's due, on every kiosk at its location. Driven every 30
/// seconds by AudioClipScheduleHostedService.
/// </summary>
/// <remarks>
/// Each play is recorded on the schedule (LastFiredOccurrenceKey) before the clip is sent, so a
/// restart or an overlapping tick can't play it twice; at worst a crash between the two loses one
/// play, which is better than a doubled announcement. A play missed by more than
/// AudioClipScheduleRules.LateBy (the server was down) is skipped.
/// </remarks>
public class AudioClipScheduleEngine(
    ISystemDbContextFactory contextFactory,
    IAudioClipPlaybackService playback,
    ILogger<AudioClipScheduleEngine> logger) : IAudioClipScheduleEngine
{
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public async Task TickAsync(DateTime utcNow, CancellationToken ct)
    {
        await _tickLock.WaitAsync(ct);

        try
        {
            DateTime venueLocal = RotaTime.ToVenueLocal(utcNow);

            await using ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync(ct);

            // Tracked: the ones that play get their key written back.
            List<AudioClipSchedule> schedules = await ctx.AudioClipSchedules
                .TagWithCallSite()
                .Include(x => x.AudioClip)
                .Where(x => x.IsEnabled && x.DeleteDate == null && x.AudioClip!.DeleteDate == null
                    && ctx.Locations.Any(l => l.Id == x.LocationId && l.IsActive))
                .ToListAsync(ct);

            if (schedules.Count == 0)
            {
                return;
            }

            List<int> locationIds = schedules.Select(x => x.LocationId).Distinct().ToList();

            Dictionary<int, List<LocationOpeningHours>> hoursByLocation = (await ctx.LocationOpeningHours
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => locationIds.Contains(x.LocationId) && x.IsActive)
                    .ToListAsync(ct))
                .GroupBy(x => x.LocationId)
                .ToDictionary(x => x.Key, x => x.ToList());

            List<(AudioClipSchedule Schedule, DateTime At)> due = [];

            foreach (AudioClipSchedule schedule in schedules)
            {
                List<LocationOpeningHours> hours = hoursByLocation.GetValueOrDefault(schedule.LocationId) ?? [];

                if (AudioClipScheduleRules.DueAt(schedule, hours, venueLocal) is not DateTime at)
                {
                    continue;
                }

                string key = AudioClipScheduleRules.OccurrenceKey(schedule, at);

                if (schedule.LastFiredOccurrenceKey == key)
                {
                    continue;
                }

                schedule.LastFiredOccurrenceKey = key;
                due.Add((schedule, at));
            }

            if (due.Count == 0)
            {
                return;
            }

            await ctx.SaveChangesAsync(ct);

            foreach ((AudioClipSchedule schedule, DateTime at) in due)
            {
                await PlayAsync(ctx, schedule, at, ct);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task PlayAsync(ApplicationDbContext ctx, AudioClipSchedule schedule, DateTime at, CancellationToken ct)
    {
        try
        {
            List<Guid> kiosks = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == schedule.LocationId && x.CompanyId == schedule.CompanyId)
                .Select(x => x.Id)
                .ToListAsync(ct);

            if (kiosks.Count == 0)
            {
                logger.LogWarning("Audio clip schedule {ScheduleId} ({Name}) was due at {At} but location {LocationId} has no kiosks", schedule.Id, schedule.Name, at, schedule.LocationId);
                return;
            }

            logger.LogInformation("Audio clip schedule {ScheduleId} ({Name}) due at {At}: playing clip {ClipId} on {Count} kiosks", schedule.Id, schedule.Name, at, schedule.AudioClipId, kiosks.Count);

            Result<List<AudioClipKioskOutcome>> result = await playback.PlayAsync(
                new AudioClipPlayRequest(schedule.AudioClipId, kiosks, schedule.CompanyId, AudioClipPlaySource.Schedule, null, schedule.Id), ct);

            if (!result.IsSuccess)
            {
                logger.LogWarning("Audio clip schedule {ScheduleId} couldn't play: {Error}", schedule.Id, result.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One schedule failing mustn't stop the others in the same tick.
            logger.LogError(ex, "Audio clip schedule {ScheduleId} failed to play", schedule.Id);
        }
    }
}
