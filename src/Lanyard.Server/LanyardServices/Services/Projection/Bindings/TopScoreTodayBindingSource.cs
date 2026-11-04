using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// "Today's top score: Gun 7 – 4,520". The earliest of equal scores wins, so a later game matching
/// the record doesn't take it over.
/// </summary>
public class TopScoreTodayBindingSource(ISystemDbContextFactory factory) : IProjectionBindingSource
{
    private readonly ISystemDbContextFactory _factory = factory;

    public string Key => ProjectionBindingKeys.TopScoreToday;

    public async Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            IQueryable<GameResult> games = GameBindingScope.GamesToday(ctx, context);

            GameResultPlayerScore? best = await ctx.GameResultPlayerScores
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => games.Any(g => g.Id == s.GameResultId))
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.GameResult!.PlayedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (best is null)
            {
                return Result<string?>.Ok(null);
            }

            return Result<string?>.Ok($"Today's top score: {GameBindingScope.PlayerLabel(best)} – {best.Score.ToString("N0", GameBindingScope.Uk)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string?>.Fail($"Couldn't load today's top score: {ex.Message}");
        }
    }
}
