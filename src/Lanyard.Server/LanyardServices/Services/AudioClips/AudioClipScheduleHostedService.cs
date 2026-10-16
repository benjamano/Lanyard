using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.AudioClips;

// Every 30 seconds, asks the engine to play any audio clip schedule that's due. A play may be up
// to two minutes late (AudioClipScheduleRules.LateBy), so a 30-second tick never misses one.
public class AudioClipScheduleHostedService(
    IAudioClipScheduleEngine engine,
    TimeProvider timeProvider,
    ILogger<AudioClipScheduleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AudioClipScheduleHostedService started");

        try
        {
            using PeriodicTimer timer = new(Interval, timeProvider);

            do
            {
                try
                {
                    await engine.TickAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per tick, so one bad tick can't stop the schedules for good.
                    logger.LogError(ex, "Playing the audio clip schedules failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("AudioClipScheduleHostedService stopped");
    }
}
