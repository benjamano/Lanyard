using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.AudioClips;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services.AudioClips;

public class AudioClipPlaybackService(
    ISystemDbContextFactory contextFactory,
    IHubContext<SignalRControlHub> hubContext,
    TimeProvider timeProvider,
    ILogger<AudioClipPlaybackService> logger) : IAudioClipPlaybackService
{
    private const int ErrorMaxLength = 500;

    private readonly ConcurrentDictionary<Guid, AudioClipNowPlaying> _nowPlaying = new();

    public event Action<Guid>? OnClipPlaybackChanged;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<Result<List<AudioClipKioskOutcome>>> PlayAsync(AudioClipPlayRequest request, CancellationToken ct = default)
    {
        try
        {
            List<Guid> clientIds = request.ClientIds.Distinct().ToList();

            if (clientIds.Count == 0)
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("Choose at least one kiosk.");
            }

            await using ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync(ct);

            // The system context sees every company, so the company is checked by hand throughout.
            AudioClip? clip = await ctx.AudioClips
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.FileMetadata)
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.Id == request.ClipId && x.CompanyId == request.CompanyId && x.DeleteDate == null, ct);

            if (clip is null)
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("Audio clip not found.");
            }

            string? extension = clip.FileMetadata is { IsActive: true } file ? AudioClipFormats.ExtensionOf(file.FileName) : null;

            if (extension is null)
            {
                return Result<List<AudioClipKioskOutcome>>.Fail("This clip's audio file is missing or isn't a format kiosks can play.");
            }

            var kiosks = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => clientIds.Contains(x.Id) && x.CompanyId == request.CompanyId)
                .Select(x => new { x.Id, x.Name, x.LocationId, x.MostRecentConnectionId, x.ReportedClientVersion })
                .ToListAsync(ct);

            DateTime utcNow = UtcNow;
            List<AudioClipKioskOutcome> outcomes = [];
            List<(AudioClipPlay Play, string ConnectionId, string ClientName)> toSend = [];

            foreach (Guid clientId in clientIds.Where(id => kiosks.All(k => k.Id != id)))
            {
                outcomes.Add(new AudioClipKioskOutcome(clientId, "Unknown kiosk", AudioClipPlayStatus.Failed, "Kiosk not found.", null));
            }

            foreach (var kiosk in kiosks)
            {
                AudioClipPlay play = new()
                {
                    Id = Guid.NewGuid(),
                    CompanyId = request.CompanyId,
                    AudioClipId = clip.Id,
                    ClientId = kiosk.Id,
                    Source = request.Source,
                    TriggeredByUserId = request.TriggeredByUserId,
                    RequestedAtUtc = utcNow
                };

                if (clip.LocationId is int clipLocationId && kiosk.LocationId != clipLocationId)
                {
                    play.Status = AudioClipPlayStatus.Failed;
                    play.Error = $"This clip only plays at {clip.Location?.Name ?? "its own location"}.";
                }
                else if (!IsRecentEnough(kiosk.ReportedClientVersion))
                {
                    play.Status = AudioClipPlayStatus.OutdatedKiosk;
                    play.Error = $"Needs kiosk version {AudioClipFormats.MinimumClientVersion} or later.";
                }
                else if (!IsConnected(kiosk.MostRecentConnectionId))
                {
                    play.Status = AudioClipPlayStatus.Offline;
                    play.Error = "Kiosk isn't connected.";
                }
                else
                {
                    play.Status = AudioClipPlayStatus.Sent;
                    toSend.Add((play, kiosk.MostRecentConnectionId!, kiosk.Name));
                }

                if (play.Status != AudioClipPlayStatus.Sent)
                {
                    play.FinishedAtUtc = utcNow;
                    outcomes.Add(new AudioClipKioskOutcome(kiosk.Id, kiosk.Name, play.Status, play.Error, play.Id));
                }

                ctx.AudioClipPlays.Add(play);
            }

            // Saved before sending, so a kiosk that reports back straight away finds its play.
            await ctx.SaveChangesAsync(ct);

            foreach ((AudioClipPlay play, string connectionId, string clientName) in toSend)
            {
                AudioClipPlayCommand command = new()
                {
                    PlayId = play.Id,
                    ClipId = clip.Id,
                    ContentVersion = clip.FileMetadataId!.Value.ToString("N"),
                    FileExtension = extension,
                    Name = clip.Name,
                    DurationSeconds = clip.DurationSeconds,
                    Volume = Math.Clamp(clip.Volume, 0, 100),
                    DuckToPercent = Math.Clamp(clip.EffectiveDuckToPercent, 0, 100),
                    FadeDownMs = Math.Max(0, clip.EffectiveFadeDownMs),
                    FadeUpMs = Math.Max(0, clip.EffectiveFadeUpMs)
                };

                // Replaces whatever the kiosk was playing: a new clip cuts the old one off, and the
                // kiosk reports that play as stopped.
                AudioClipNowPlaying nowPlaying = new(play.Id, clip.Id, clip.Name, AudioClipPlayStatus.Sent, utcNow);
                _nowPlaying[play.ClientId] = nowPlaying;

                try
                {
                    logger.LogInformation("Dispatching {Method} for clip {ClipId} to client {ClientId} (connection {ConnectionId}), play {PlayId}",
                        ClientHubMethods.PlayAudioClip, clip.Id, play.ClientId, connectionId, play.Id);

                    await hubContext.Clients.Client(connectionId).SendAsync(ClientHubMethods.PlayAudioClip, command, ct);

                    outcomes.Add(new AudioClipKioskOutcome(play.ClientId, clientName, AudioClipPlayStatus.Sent, null, play.Id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to send clip {ClipId} to client {ClientId}", clip.Id, play.ClientId);

                    _nowPlaying.TryRemove(new KeyValuePair<Guid, AudioClipNowPlaying>(play.ClientId, nowPlaying));

                    play.Status = AudioClipPlayStatus.Failed;
                    play.Error = Truncate($"Couldn't reach the kiosk: {ex.Message}");
                    play.FinishedAtUtc = UtcNow;
                    await ctx.SaveChangesAsync(CancellationToken.None);

                    outcomes.Add(new AudioClipKioskOutcome(play.ClientId, clientName, AudioClipPlayStatus.Failed, play.Error, play.Id));
                }

                OnClipPlaybackChanged?.Invoke(play.ClientId);
            }

            logger.LogInformation("Audio clip {ClipId} played on {Sent} of {Requested} kiosks ({Source})",
                clip.Id, outcomes.Count(x => x.Status == AudioClipPlayStatus.Sent), clientIds.Count, request.Source);

            return Result<List<AudioClipKioskOutcome>>.Ok(outcomes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to play audio clip {ClipId}", request.ClipId);
            return Result<List<AudioClipKioskOutcome>>.Fail($"Failed to play the clip: {ex.Message}");
        }
    }

    public async Task<Result<bool>> StopAsync(Guid clientId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync();

            var kiosk = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId && x.CompanyId == companyId)
                .Select(x => new { x.MostRecentConnectionId })
                .FirstOrDefaultAsync();

            if (kiosk is null)
            {
                return Result<bool>.Fail("Kiosk not found.");
            }

            if (!IsConnected(kiosk.MostRecentConnectionId))
            {
                return Result<bool>.Fail("Kiosk isn't connected.");
            }

            // Guid.Empty stops whatever is playing: after a server restart the kiosk may be
            // playing a clip this process never heard about.
            Guid playId = _nowPlaying.TryGetValue(clientId, out AudioClipNowPlaying? current) ? current.PlayId : Guid.Empty;

            logger.LogInformation("Dispatching {Method} to client {ClientId}, play {PlayId}", ClientHubMethods.StopAudioClip, clientId, playId);

            await hubContext.Clients.Client(kiosk.MostRecentConnectionId!).SendAsync(ClientHubMethods.StopAudioClip, playId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop the audio clip on client {ClientId}", clientId);
            return Result<bool>.Fail($"Failed to stop the clip: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ReportFromClientAsync(Guid clientId, AudioClipPlaybackReport report)
    {
        try
        {
            await using ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync();

            AudioClipPlay? play = await ctx.AudioClipPlays
                .FirstOrDefaultAsync(x => x.Id == report.PlayId && x.ClientId == clientId);

            if (play is null)
            {
                logger.LogWarning("Client {ClientId} reported clip play {PlayId}, which wasn't sent to it; ignoring", clientId, report.PlayId);
                return Result<bool>.Ok(false);
            }

            // A late report can't bring a finished play back to life.
            if (IsFinal(play.Status))
            {
                return Result<bool>.Ok(false);
            }

            AudioClipPlayStatus status = ToPlayStatus(report.Status);

            play.Status = status;
            play.Error = report.Error is null ? null : Truncate(report.Error);

            if (IsFinal(status))
            {
                play.FinishedAtUtc = UtcNow;
            }

            await ctx.SaveChangesAsync();

            if (_nowPlaying.TryGetValue(clientId, out AudioClipNowPlaying? current) && current.PlayId == report.PlayId)
            {
                if (IsFinal(status))
                {
                    _nowPlaying.TryRemove(new KeyValuePair<Guid, AudioClipNowPlaying>(clientId, current));
                }
                else
                {
                    _nowPlaying.TryUpdate(clientId, current with { Status = status }, current);
                }
            }

            logger.LogInformation("Client {ClientId} reported clip play {PlayId}: {Status} {Error}", clientId, report.PlayId, status, play.Error);

            OnClipPlaybackChanged?.Invoke(clientId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record client {ClientId}'s report for clip play {PlayId}", clientId, report.PlayId);
            return Result<bool>.Fail($"Failed to record the report: {ex.Message}");
        }
    }

    public AudioClipNowPlaying? GetNowPlaying(Guid clientId) =>
        _nowPlaying.TryGetValue(clientId, out AudioClipNowPlaying? current) ? current : null;

    // SignalRControlHub.ConnectedIds is process-static, so tests override this, the same seam
    // MusicScheduleEngine gives its own tests.
    protected internal virtual bool IsConnected(string? connectionId) =>
        !string.IsNullOrEmpty(connectionId) && SignalRControlHub.ConnectedIds.Contains(connectionId);

    private static bool IsRecentEnough(string? reportedVersion) =>
        ClientVersionFormat.TryNormalize(reportedVersion, out string version)
        && ClientVersionFormat.Compare(version, AudioClipFormats.MinimumClientVersion) >= 0;

    private static bool IsFinal(AudioClipPlayStatus status) => status is
        AudioClipPlayStatus.Finished or AudioClipPlayStatus.Stopped or AudioClipPlayStatus.Failed
        or AudioClipPlayStatus.Offline or AudioClipPlayStatus.OutdatedKiosk;

    private static AudioClipPlayStatus ToPlayStatus(AudioClipPlaybackStatus status) => status switch
    {
        AudioClipPlaybackStatus.Preparing => AudioClipPlayStatus.Preparing,
        AudioClipPlaybackStatus.Playing => AudioClipPlayStatus.Playing,
        AudioClipPlaybackStatus.Finished => AudioClipPlayStatus.Finished,
        AudioClipPlaybackStatus.Stopped => AudioClipPlayStatus.Stopped,
        _ => AudioClipPlayStatus.Failed
    };

    private static string Truncate(string value) => value.Length <= ErrorMaxLength ? value : value[..ErrorMaxLength];
}
