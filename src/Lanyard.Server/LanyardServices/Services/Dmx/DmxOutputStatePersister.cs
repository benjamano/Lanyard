using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Writes each kiosk's DMX universe, master and blackout back to the database every couple of
/// seconds while they change, and once more on shutdown, so a restart restores the lights. Write-
/// behind rather than per change: a dragged fader changes 40 times a second.
/// </summary>
public sealed class DmxOutputStatePersister(DmxService engine, ILogger<DmxOutputStatePersister> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await engine.PersistDirtyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; StopAsync does the final write.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await engine.PersistDirtyAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't save DMX state on shutdown");
        }
    }
}
