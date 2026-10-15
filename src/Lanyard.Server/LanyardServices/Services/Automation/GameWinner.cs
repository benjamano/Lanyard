#nullable enable

using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

/// <summary>Which team won a finished game, from its final scores.</summary>
public static class GameWinner
{
    /// <summary>
    /// The team with the strictly highest total score, or null when there's no single winner: a
    /// draw, a solo game (no players on a team), a game where only one team played, or an empty
    /// game (a false start). Players with no team don't count towards any team.
    /// </summary>
    public static Team? Determine(IReadOnlyList<PlayerScoreSnapshot> players)
    {
        List<(Team Team, int Total)> totals = players
            .Where(p => p.Team != null)
            .GroupBy(p => p.Team!.Value)
            .Select(g => (Team: g.Key, Total: g.Sum(p => p.Score)))
            .OrderByDescending(t => t.Total)
            .ToList();

        // Beating nobody isn't winning.
        if (totals.Count < 2)
        {
            return null;
        }

        return totals[0].Total > totals[1].Total ? totals[0].Team : null;
    }
}
