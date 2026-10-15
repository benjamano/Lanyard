#nullable enable

using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

public sealed record PlayerScoreSnapshot(int GunId, Team? Team, int Score);

/// <summary>
/// An immutable copy of one kiosk status publish, as the automation engine sees it.
/// </summary>
/// <param name="GameNumber">
/// Which game on this kiosk the publish belongs to, counted by the engine on every move into
/// InGame. Game-data rules fire at most once per game number, so the kiosk republishing the same
/// state many times a second can't re-fire them.
/// </param>
public sealed record GameSnapshot(
    Guid ClientId,
    int GameNumber,
    GameStatus Status,
    int TimeRemainingSeconds,
    IReadOnlyList<PlayerScoreSnapshot> Players)
{
    // Copied rather than kept: the DTO is a mutable object the status store also holds.
    public static GameSnapshot From(Guid clientId, int gameNumber, LaserGameStatusDTO status)
    {
        return new GameSnapshot(
            clientId,
            gameNumber,
            status.Status,
            status.TimeRemainingSeconds,
            [.. status.PlayerScores.Select(p => new PlayerScoreSnapshot(p.GunId, p.Team, p.Score))]);
    }
}

/// <summary>Signals "this kiosk has a newer snapshot"; the snapshot itself is read when processed.</summary>
public sealed record GameDataEvent(Guid ClientId);
