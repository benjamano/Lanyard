using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

// Append-only history of finished laser games. Written once, at the InGame -> NotStarted edge,
// and never mutated or soft-deleted afterwards - so unlike most models here there is no IsActive
// or DeleteDate. Live in-progress scores stay in the in-memory LaserGameStatusStore; only
// completed games land in the database.
public class GameResult
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public DateTime PlayedAtUtc { get; set; }

    // Snapshot of the game's configured length, taken from LaserGameStatusDTO.TotalTimeSeconds.
    public int DurationSeconds { get; set; }

    public virtual List<GameResultPlayerScore> PlayerScores { get; set; } = [];
}

public class GameResultPlayerScore
{
    public Guid Id { get; set; }

    public Guid GameResultId { get; set; }
    public GameResult? GameResult { get; set; }

    // The hardware identifies players by gun, not by person - there is no per-person identity
    // available from the packet stream, so leaderboards are per-gun.
    public int GunId { get; set; }

    // Always null today: nothing populates a player name anywhere in the pipeline. Carried on the
    // table from the start so name capture can be added later without a migration.
    public string? PlayerName { get; set; }

    public int Score { get; set; }
    public int Accuracy { get; set; }

    // Nullable because Team has no "unassigned" member (Red = 0, Green = 2, no value 1), matching
    // PlayerScoreDTO.Team.
    public Team? Team { get; set; }
}
