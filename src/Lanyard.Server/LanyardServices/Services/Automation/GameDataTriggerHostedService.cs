#nullable enable

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// The single consumer of the automation engine's game-data channel (TimeRemainingBelow and
/// ScoreThreshold rules). Kept apart from AutomationEngineHostedService so a burst of game data can
/// never delay a status-transition rule, and vice versa.
/// </summary>
public class GameDataTriggerHostedService(
    AutomationEngineService engineService,
    ILogger<GameDataTriggerHostedService> logger) : BackgroundService
{
    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<GameDataTriggerHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("GameDataTriggerHostedService started");

            await foreach (GameDataEvent gameDataEvent in _engineService.GameDataReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _engineService.ProcessGameDataAsync(gameDataEvent, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing game data for client {ClientId}", gameDataEvent.ClientId);
                }
            }

            _logger.LogInformation("GameDataTriggerHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GameDataTriggerHostedService terminated with an unhandled exception");
        }
    }
}
