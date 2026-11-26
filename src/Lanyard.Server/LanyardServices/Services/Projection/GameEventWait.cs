using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services.Projection;

/// <summary>
/// Decides when a "Wait for Game" step has seen its event, from the laser kiosk's status
/// publishes in order. Pure and thread-safe: the runner feeds it from the status store's event
/// (on the SignalR hub's thread) and from its own hold loop, and only ever reads
/// <see cref="Satisfied"/>, which latches once true.
/// </summary>
/// <remarks>
/// The first status seen is the starting point. A kiosk with no status (never connected, or
/// disconnected) gives nothing to go on, so null is ignored and the step's timeout takes over.
/// </remarks>
public sealed class GameEventWait(ProjectionGameEvent gameEvent, Guid kioskClientId, int? secondsLeft = null)
{
    private readonly object _lock = new();

    private bool _hasBaseline;
    private GameStatus _lastStatus;
    private bool _satisfied;

    public ProjectionGameEvent Event { get; } = gameEvent;
    public Guid KioskClientId { get; } = kioskClientId;

    public bool Satisfied
    {
        get
        {
            lock (_lock)
            {
                return _satisfied;
            }
        }
    }

    /// <summary>Feeds one status publish. Publishes from other kiosks and null are ignored.</summary>
    public void Observe(LaserGameStatusDTO? status)
    {
        if (status == null || status.ClientId != KioskClientId)
        {
            return;
        }

        lock (_lock)
        {
            if (_satisfied)
            {
                return;
            }

            bool isFirst = !_hasBaseline;
            GameStatus previous = _lastStatus;

            _hasBaseline = true;
            _lastStatus = status.Status;

            _satisfied = isFirst ? IsMetAtStart(status) : IsMetByChange(previous, status);
        }
    }

    private bool IsMetAtStart(LaserGameStatusDTO status) => Event switch
    {
        ProjectionGameEvent.GetReady => status.Status is GameStatus.GetReady or GameStatus.InGame,
        ProjectionGameEvent.GameStarts => status.Status == GameStatus.InGame,

        // Nothing to wait for when no game is running.
        ProjectionGameEvent.GameEnds => status.Status != GameStatus.InGame,
        ProjectionGameEvent.TimeLeftBelow => IsTimeLeftAtOrBelow(status),
        _ => false
    };

    private bool IsMetByChange(GameStatus previous, LaserGameStatusDTO status)
    {
        bool gameJustEnded = previous == GameStatus.InGame && status.Status != GameStatus.InGame;

        return Event switch
        {
            // Going straight into a game counts, so a missed Get ready can't cost a whole game.
            ProjectionGameEvent.GetReady => status.Status is GameStatus.GetReady or GameStatus.InGame,
            ProjectionGameEvent.GameStarts => status.Status == GameStatus.InGame,
            ProjectionGameEvent.GameEnds => gameJustEnded,

            // A game stopped early never reaches the time, so it ends the wait too rather than
            // holding the step until some later game.
            ProjectionGameEvent.TimeLeftBelow => IsTimeLeftAtOrBelow(status) || gameJustEnded,
            _ => false
        };
    }

    // A game's first In game publish carries 0 seconds left (the kiosk clears the countdown when
    // a game ends and only learns the new one from the next countdown packet), so 0 never counts.
    private bool IsTimeLeftAtOrBelow(LaserGameStatusDTO status) =>
        status.Status == GameStatus.InGame
        && status.TimeRemainingSeconds > 0
        && status.TimeRemainingSeconds <= (secondsLeft ?? 0);
}
