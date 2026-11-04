using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// "Red team won with 12,340" for a team game, "Gun 7 won with 4,520" for a solo one, from the most
/// recent game today. Nothing (so the fallback shows) until the first game of the day finishes.
/// </summary>
public class LastGameWinnerBindingSource(ISystemDbContextFactory factory) : IProjectionBindingSource
{
    private readonly ISystemDbContextFactory _factory = factory;

    public string Key => ProjectionBindingKeys.LastGameWinner;

    public async Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            GameResult? game = await GameBindingScope.GamesToday(ctx, context)
                .AsNoTracking()
                .TagWithCallSite()
                .Include(g => g.PlayerScores)
                .OrderByDescending(g => g.PlayedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (game is null || game.PlayerScores.Count == 0)
            {
                return Result<string?>.Ok(null);
            }

            return Result<string?>.Ok(DescribeWinner(game.PlayerScores));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string?>.Fail($"Couldn't load the last game: {ex.Message}");
        }
    }

    public static string DescribeWinner(IReadOnlyCollection<GameResultPlayerScore> scores)
    {
        List<(Team Team, int Total)> teams = [.. scores
            .Where(s => s.Team.HasValue)
            .GroupBy(s => s.Team!.Value)
            .Select(g => (g.Key, g.Sum(s => s.Score)))
            .OrderByDescending(t => t.Item2)];

        if (teams.Count > 0)
        {
            if (teams.Count > 1 && teams[0].Total == teams[1].Total)
            {
                return "It's a draw!";
            }

            return $"{teams[0].Team} team won with {teams[0].Total.ToString("N0", GameBindingScope.Uk)}";
        }

        List<GameResultPlayerScore> players = [.. scores.OrderByDescending(s => s.Score)];

        if (players.Count > 1 && players[0].Score == players[1].Score)
        {
            return "It's a draw!";
        }

        return $"{GameBindingScope.PlayerLabel(players[0])} won with {players[0].Score.ToString("N0", GameBindingScope.Uk)}";
    }
}
