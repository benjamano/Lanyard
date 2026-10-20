using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.MusicScheduling;

// Every 30 seconds, asks the engine to bring each scheduled kiosk onto its current part of the
// day. The first look is straight away, so a restart picks the schedule up again at once (kiosks
// that haven't reconnected yet are caught on a later tick).
public class MusicScheduleHostedService(
    IMusicScheduleEngine engine,
    TimeProvider timeProvider,
    ILogger<MusicScheduleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IMusicScheduleEngine _engine = engine;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<MusicScheduleHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MusicScheduleHostedService started");

        try
        {
            using PeriodicTimer timer = new(Interval, _timeProvider);

            do
            {
                try
                {
                    await _engine.TickAsync(_timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per tick, so one bad tick can't stop the schedule for good.
                    _logger.LogError(ex, "Applying the music schedules failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("MusicScheduleHostedService stopped");
    }
}
