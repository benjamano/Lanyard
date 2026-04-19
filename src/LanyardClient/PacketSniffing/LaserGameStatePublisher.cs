using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.PacketSniffing;

public class LaserGameStatePublisher(
    IGameStateService gameStateService,
    ISignalRClient signalRClient,
    ILogger<LaserGameStatePublisher> logger) : ILaserGameStatePublisher
{
    private readonly IGameStateService _gameStateService = gameStateService;
    private readonly ISignalRClient _signalRClient = signalRClient;
    private readonly ILogger<LaserGameStatePublisher> _logger = logger;

    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public void Register()
    {
        _gameStateService.GameStateChanged += OnGameStateChanged;
    }

    public async Task PublishAsync()
    {
        await _publishLock.WaitAsync();

        try
        {
            string? clientIdValue = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID");
            if (!Guid.TryParse(clientIdValue, out Guid clientId))
            {
                return;
            }

            LaserGameStatusDTO status = new()
            {
                ClientId = clientId,
                Status = _gameStateService.GetGameStatus(),
                TimeRemainingSeconds = (int)Math.Max(0, _gameStateService.GetTimeRemaining().TotalSeconds),
                PlayerCount = _gameStateService.GetAllPlayerScores().Count,
                LastUpdateUtc = DateTime.UtcNow
            };

            await _signalRClient.SendLaserGameStatusAsync(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish laser game status to server.");
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private void OnGameStateChanged()
    {
        _ = PublishAsync();
    }
}
