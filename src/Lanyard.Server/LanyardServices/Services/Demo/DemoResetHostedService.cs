using Lanyard.Infrastructure.Enum;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Demo;

// Seeds the demo company the first time the server starts with Demo:Enabled, then wipes and
// reseeds it once a day at DemoOptions.ResetTimeUk (UK time). A reset that was missed while the
// server was down happens on the next tick after it's back.
public sealed class DemoResetHostedService(
    IDemoResetService resetService,
    IOptions<DemoOptions> demoOptions,
    TimeProvider timeProvider,
    ILogger<DemoResetHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!demoOptions.Value.Enabled)
        {
            return;
        }

        try
        {
            logger.LogInformation("DemoResetHostedService started; the demo resets daily at {ResetTime} UK time", demoOptions.Value.ResetTimeUk);

            await ResetIfDueAsync(stoppingToken);

            using PeriodicTimer timer = new(TickInterval, timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ResetIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DemoResetHostedService terminated with an unhandled exception");
        }
    }

    private async Task ResetIfDueAsync(CancellationToken ct)
    {
        try
        {
            DateTime? lastResetUtc = await resetService.GetLastResetUtcAsync(ct);

            if (!IsResetDue(lastResetUtc, timeProvider.GetUtcNow().UtcDateTime, demoOptions.Value.ResetTimeUk))
            {
                return;
            }

            await resetService.ResetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per tick, so one failed reset doesn't stop the next day's.
            logger.LogError(ex, "Scheduled demo reset failed");
        }
    }

    // Due when it's never been seeded, or when the most recent reset time (today's if it has
    // passed, otherwise yesterday's) is later than the last reset.
    public static bool IsResetDue(DateTime? lastResetUtc, DateTime nowUtc, TimeOnly resetTimeUk)
    {
        if (lastResetUtc is null)
        {
            return true;
        }

        DateOnly todayUk = RotaTime.Today(nowUtc);
        DateTime todaysResetUtc = RotaTime.ToUtc(todayUk, resetTimeUk);
        DateTime latestScheduledUtc = nowUtc >= todaysResetUtc ? todaysResetUtc : RotaTime.ToUtc(todayUk.AddDays(-1), resetTimeUk);

        return lastResetUtc.Value < latestScheduledUtc;
    }
}
