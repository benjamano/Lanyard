namespace Lanyard.Infrastructure.Enum;

/// <summary>
/// What a "Wait for Game" projection step waits for on its laser kiosk. Stored by name in the
/// step's "Game Event" parameter, so the names must not change.
/// </summary>
public enum ProjectionGameEvent
{
    /// <summary>The kiosk enters Get ready, or goes straight into a game.</summary>
    GetReady,

    /// <summary>A game is in progress.</summary>
    GameStarts,

    /// <summary>A game that was running finishes. Ends at once if no game is running.</summary>
    GameEnds,

    /// <summary>Time remaining in a running game drops to the step's "Seconds Left" or less.</summary>
    TimeLeftBelow
}
