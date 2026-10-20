using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.LaserGame;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.PacketSniffing;

// Every game packet changes the state, and each send is a hub round trip carrying every player's
// score. The coalescer sends status changes straight away and in order, and merges everything else
// into at most one send per MinInterval. One loop does all the sending, so sends never overlap.
public class LaserGameStatePublisher : ILaserGameStatePublisher
{
    // The status widget counts down by itself between updates, so 4 a second is plenty.
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    private readonly IGameStateService _gameStateService;
    private readonly ISignalRClient _signalRClient;
    private readonly ILogger<LaserGameStatePublisher> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly LaserStatusPublishCoalescer _coalescer;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Guid? _clientId;

    private GameStatus? _lastSentStatus;
    private int _loopStarted;

    public LaserGameStatePublisher(
        IGameStateService gameStateService,
        ISignalRClient signalRClient,
        ILogger<LaserGameStatePublisher> logger,
        TimeProvider timeProvider)
    {
        _gameStateService = gameStateService;
        _signalRClient = signalRClient;
        _logger = logger;
        _timeProvider = timeProvider;

        _coalescer = new LaserStatusPublishCoalescer(_gameStateService.GetCurrentStatus, MinInterval);
        _clientId = Guid.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID"), out Guid clientId) ? clientId : null;
    }

    public void Register()
    {
        _gameStateService.GameStateChanged += OnGameStateChanged;

        if (Interlocked.Exchange(ref _loopStarted, 1) == 0)
        {
            _ = Task.Run(SendLoopAsync);
        }
    }

    public Task PublishAsync()
    {
        _coalescer.SendNow();
        _wake.Release();

        return Task.CompletedTask;
    }

    private void OnGameStateChanged()
    {
        // Runs on the capture thread. A status change is snapshotted inside Notify, before the
        // game state carries on (e.g. clears the finished game's countdown).
        if (_coalescer.Notify(_gameStateService.GetGameStatus()))
        {
            _wake.Release();
        }
    }

    private async Task SendLoopAsync()
    {
        while (true)
        {
            try
            {
                LaserGameStatusDTO? next = _coalescer.TakeNext(_timeProvider.GetUtcNow().UtcDateTime, out TimeSpan? retryAfter);

                if (next != null)
                {
                    await SendAsync(next);
                }
                else if (retryAfter != null)
                {
                    // A status change wakes this early.
                    await _wake.WaitAsync(retryAfter.Value);
                }
                else
                {
                    await _wake.WaitAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Laser game status send loop failed; carrying on.");
            }
        }
    }

    private async Task SendAsync(LaserGameStatusDTO status)
    {
        if (_clientId == null)
        {
            return;
        }

        LogGameSummaryIfEnded(status.Status);

        // While disconnected each send would only fail. The kiosk sends its current status again
        // when it reconnects.
        if (!_signalRClient.IsConnected)
        {
            return;
        }

        try
        {
            status.ClientId = _clientId.Value;

            await _signalRClient.SendLaserGameStatusAsync(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish laser game status to server.");
        }
    }

    private void LogGameSummaryIfEnded(GameStatus status)
    {
        GameStatus? previous = _lastSentStatus;
        _lastSentStatus = status;

        if (previous == status)
        {
            return;
        }

        (int changes, int sent) = _coalescer.TakeCounters();

        if (previous == GameStatus.InGame && status == GameStatus.NotStarted)
        {
            _logger.LogInformation("Laser game ended: {Changes} game state changes, {Sent} status updates sent to the server.", changes, sent);
        }
    }
}
