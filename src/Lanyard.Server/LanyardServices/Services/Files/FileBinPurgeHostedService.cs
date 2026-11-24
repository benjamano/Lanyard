using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Deletes for good whatever has been in the Files Recycle bin for longer than
/// <see cref="FileLibraryRules.BinRetention"/>. Runs in its own scope with nobody signed in, which
/// the tenant context treats as system, so one sweep covers every company. Anything still in use
/// is left in the bin (the bin says so) rather than deleted from under the feature using it.
/// </summary>
public sealed class FileBinPurgeHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<FileBinPurgeHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(6);

    // Let startup (migrations, seeding, the first requests) settle before the first sweep.
    private static readonly TimeSpan _firstSweepDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("FileBinPurgeHostedService started");

            await Task.Delay(_firstSweepDelay, timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FileBinPurgeHostedService terminated with an unhandled exception");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            IFileLibraryService library = scope.ServiceProvider.GetRequiredService<IFileLibraryService>();

            Result<BulkOperationResult> result = await library.PurgeExpiredAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);

            if (!result.IsSuccess)
                logger.LogWarning("Recycle bin purge failed: {Error}", result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recycle bin purge sweep failed");
        }
    }
}
