#nullable enable

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Ticks the automation engine's scheduled-rule evaluation.
/// </summary>
/// <remarks>
/// Same "no inbound event, needs a clock" reasoning as IdleTriggerHostedService, kept as its own
/// hosted service rather than folded into that one: the name and doc on that class are specifically
/// about idle detection, and per-tick exception isolation means a failing idle evaluation can't
/// skip a scheduled evaluation in the same tick, or vice versa. DateTime.Now (not UtcNow) is
/// intentional - scheduled rules fire on server-local wall-clock time, matching
/// HallOfFamePeriodExtensions.ToUtcLowerBound and Client.AutoRestartTimeOfDay.
/// </remarks>
public class ScheduledTriggerHostedService(
    AutomationEngineService engineService,
    ILogger<ScheduledTriggerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<ScheduledTriggerHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("ScheduledTriggerHostedService started");

            using PeriodicTimer timer = new(TickInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _engineService.ProcessScheduledRulesAsync(DateTime.Now, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Swallowed per tick, matching IdleTriggerHostedService: one bad rule must not
                    // take the loop down and silently stop every other scheduled rule.
                    _logger.LogError(ex, "Unhandled error evaluating scheduled automation rules");
                }
            }

            _logger.LogInformation("ScheduledTriggerHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScheduledTriggerHostedService terminated with an unhandled exception");
        }
    }
}
