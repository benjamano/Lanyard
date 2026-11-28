using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.DTO;

public class HallOfFameEntryDTO
{
    public int GunId { get; set; }

    // Always null today - nothing in the pipeline populates a player name. Present so the UI can
    // fall back to the gun number now and show a real name later without a shape change.
    public string? PlayerName { get; set; }

    public int Score { get; set; }
    public int Accuracy { get; set; }
    public Team? Team { get; set; }

    public DateTime PlayedAtUtc { get; set; }
}

// One finished game for the Laser game status widget's history list.
public class RecentGameDTO
{
    public Guid Id { get; set; }

    public DateTime StartedAtUtc { get; set; }

    // True when the game has no recorded start, so StartedAtUtc is PlayedAtUtc - DurationSeconds.
    // Wrong by however early the game was stopped, so the UI marks it as approximate.
    public bool StartIsEstimate { get; set; }

    public DateTime EndedAtUtc { get; set; }

    public int PlayerCount { get; set; }

    // Highest total first. Empty for a free-for-all game where nobody had a team.
    public List<RecentGameTeamTotalDTO> TeamTotals { get; set; } = [];

    // Set when a team won outright.
    public Team? WinningTeam { get; set; }

    // Set when nobody had a team, so the top-scoring gun is the winner instead.
    public int? WinningGunId { get; set; }

    public int WinningScore { get; set; }

    // Two or more teams (or guns, in a free-for-all) tied on the top score.
    public bool IsDraw { get; set; }
}

public class RecentGameTeamTotalDTO
{
    public Team Team { get; set; }
    public int TotalScore { get; set; }
}

public class HallOfFameTeamTotalDTO
{
    public Team Team { get; set; }
    public int TotalScore { get; set; }
    public int GameCount { get; set; }
}
