#nullable enable

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Ticks the automation engine's idle-rule evaluation.
/// </summary>
/// <remarks>
/// Idle rules have no inbound event to react to - "nothing happened for 30 minutes" can only be
/// noticed by looking - so unlike transition rules, which ride the hub's channel via
/// AutomationEngineHostedService, these need a clock. The tick interval only bounds how late a
/// rule can fire, not its accuracy, so a coarse minute is plenty.
/// </remarks>
public class IdleTriggerHostedService(
    AutomationEngineService engineService,
    ILogger<IdleTriggerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<IdleTriggerHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("IdleTriggerHostedService started");

            using PeriodicTimer timer = new(TickInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _engineService.ProcessIdleRulesAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Swallowed per tick, matching AutomationEngineHostedService: one bad rule
                    // must not take the loop down and silently stop every other idle rule.
                    _logger.LogError(ex, "Unhandled error evaluating idle automation rules");
                }
            }

            _logger.LogInformation("IdleTriggerHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IdleTriggerHostedService terminated with an unhandled exception");
        }
    }
}
