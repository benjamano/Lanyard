using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Background worker for BPM analysis. Songs reach it two ways: creation points
/// (file upload, playlist add) enqueue directly, and a periodic sweep picks up
/// any NotAnalyzed songs those paths missed. Terminal statuses are never
/// re-enqueued, so both are idempotent. The queue drains serially — analysis is
/// CPU-and-IO heavy and there is no urgency, so one song at a time is deliberate.
/// </summary>
public class SongAnalysisHostedService(
    ISongAnalysisQueue queue,
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopeFactory,
    ILogger<SongAnalysisHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);

    private readonly ISongAnalysisQueue _queue = queue;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<SongAnalysisHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("SongAnalysisHostedService started");

            _ = Task.Run(() => RunPeriodicSweepAsync(stoppingToken), stoppingToken);

            await foreach (Guid songId in _queue.DequeueAllAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    ISongAnalysisService analysisService = scope.ServiceProvider.GetRequiredService<ISongAnalysisService>();

                    Result<bool> result = await analysisService.AnalyzeSongAsync(songId, stoppingToken);

                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("BPM analysis for song {SongId} failed: {Error}", songId, result.Error);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error analyzing song {SongId}", songId);
                }
            }

            _logger.LogInformation("SongAnalysisHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SongAnalysisHostedService terminated with an unhandled exception");
        }
    }

    /// <summary>
    /// Sweeps immediately on startup (the original backfill) and then every few
    /// minutes, so songs created by paths that don't enqueue directly still get
    /// analyzed within one interval. Re-enqueueing an already-processed song is a
    /// no-op — the analysis service skips anything not in NotAnalyzed.
    /// </summary>
    private async Task RunPeriodicSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await EnqueueBackfillAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task EnqueueBackfillAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync(stoppingToken);

            List<Guid> pendingSongIds = await context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.IsActive && s.BpmAnalysisStatus == BpmAnalysisStatus.NotAnalyzed)
                .Select(s => s.Id)
                .ToListAsync(stoppingToken);

            if (pendingSongIds.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Enqueuing {Count} songs for BPM analysis backfill", pendingSongIds.Count);

            foreach (Guid songId in pendingSongIds)
            {
                _queue.Enqueue(songId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed backfill only delays analysis until the next restart —
            // don't take the whole worker down over it.
            _logger.LogError(ex, "BPM analysis backfill failed");
        }
    }
}
