using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Lanyard.Application.Services;

public class BeatClockService(
    MusicPlayerService musicPlayerService,
    IDbContextFactory<ApplicationDbContext> factory,
    IMemoryCache cache,
    ILogger<BeatClockService> logger) : IBeatClockService
{
    private readonly MusicPlayerService _musicPlayerService = musicPlayerService;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<BeatClockService> _logger = logger;

    // BPM data is immutable once analyzed, but the analyzer may finish while a
    // song is queued/playing — a short cache picks that up without a DB round
    // trip on every scene step.
    private static readonly TimeSpan _bpmCacheDuration = TimeSpan.FromSeconds(30);

    private sealed record SongBeatInfo(double? Bpm, double? FirstBeatOffsetSeconds);

    public async Task<TimeSpan?> GetDelayUntilNextStepAsync(Guid clientId, double beats)
    {
        try
        {
            if (_musicPlayerService.GetCurrentPlaybackState(clientId) != PlaybackState.Playing)
            {
                return null;
            }

            Song? song = _musicPlayerService.GetCurrentSong(clientId);

            if (song == null)
            {
                return null;
            }

            SongBeatInfo? beatInfo = await GetSongBeatInfoAsync(song.Id);

            if (beatInfo?.Bpm is not > 0)
            {
                return null;
            }

            double positionSeconds = _musicPlayerService.GetEstimatedPositionSeconds(clientId);

            double? delaySeconds = BeatMath.SecondsUntilNextStep(
                positionSeconds,
                beatInfo.Bpm.Value,
                beatInfo.FirstBeatOffsetSeconds ?? 0,
                beats);

            return delaySeconds.HasValue ? TimeSpan.FromSeconds(delaySeconds.Value) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing beat delay for client {ClientId}", clientId);
            return null;
        }
    }

    /// <summary>
    /// BPM and beat offset are read from the database rather than the in-memory
    /// now-playing snapshot: that snapshot is taken when the queue loads and can
    /// predate the background analysis finishing.
    /// </summary>
    private async Task<SongBeatInfo?> GetSongBeatInfoAsync(Guid songId)
    {
        string cacheKey = $"beatclock-bpm-{songId}";

        if (_cache.TryGetValue(cacheKey, out SongBeatInfo? cached))
        {
            return cached;
        }

        await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

        SongBeatInfo? beatInfo = await context.Songs
            .AsNoTracking()
            .TagWithCallSite()
            .Where(s => s.Id == songId)
            .Select(s => new SongBeatInfo(s.Bpm, s.FirstBeatOffsetSeconds))
            .FirstOrDefaultAsync();

        _cache.Set(cacheKey, beatInfo, _bpmCacheDuration);

        return beatInfo;
    }
}
