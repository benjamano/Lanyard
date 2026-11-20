#nullable enable

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Ticks the automation engine's party-time rules once a minute.
/// </summary>
/// <remarks>
/// Its own hosted service, like ScheduledTriggerHostedService, so a failing party evaluation can't
/// skip a scheduled one in the same tick or the other way round. Passes UTC: party times are stored
/// in UTC, so unlike Scheduled rules there's no venue wall-clock to convert to.
/// </remarks>
public class PartyTriggerHostedService(
    AutomationEngineService engineService,
    ILogger<PartyTriggerHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<PartyTriggerHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("PartyTriggerHostedService started");

            using PeriodicTimer timer = new(TickInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _engineService.ProcessPartyRulesAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Swallowed per tick: one bad rule must not stop every other party rule.
                    _logger.LogError(ex, "Unhandled error evaluating party automation rules");
                }
            }

            _logger.LogInformation("PartyTriggerHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PartyTriggerHostedService terminated with an unhandled exception");
        }
    }
}
