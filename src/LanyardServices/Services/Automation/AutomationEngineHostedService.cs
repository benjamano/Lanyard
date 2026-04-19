#nullable enable

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class AutomationEngineHostedService(
    AutomationEngineService engineService,
    ILogger<AutomationEngineHostedService> logger) : BackgroundService
{
    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<AutomationEngineHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutomationEngineHostedService started");

        await foreach (GameStatusTransitionEvent transitionEvent in
            _engineService.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _engineService.ProcessTransitionAsync(transitionEvent, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unhandled error processing transition event for client {ClientId} — new status {NewStatus}",
                    transitionEvent.ClientId, transitionEvent.NewStatus);
            }
        }

        _logger.LogInformation("AutomationEngineHostedService stopped");
    }
}
