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

public class HallOfFameTeamTotalDTO
{
    public Team Team { get; set; }
    public int TotalScore { get; set; }
    public int GameCount { get; set; }
}

// Everything one Hall of Fame widget shows, loaded together so a refresh is one cache lookup (and
// at most one DbContext) rather than three. Shared between circuits by HallOfFameCache, so treat
// it as read-only.
public class HallOfFameSnapshotDTO
{
    public IReadOnlyList<HallOfFameEntryDTO> TopScores { get; init; } = [];
    public IReadOnlyList<HallOfFameEntryDTO> TopAccuracy { get; init; } = [];
    public IReadOnlyList<HallOfFameTeamTotalDTO> TeamTotals { get; init; } = [];
}
