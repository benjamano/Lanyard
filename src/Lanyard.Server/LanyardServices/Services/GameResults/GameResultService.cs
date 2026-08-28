using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class GameResultService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<GameResultService> logger) : IGameResultService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<GameResultService> _logger = logger;

    public async Task<Result<bool>> RecordCompletedGameAsync(Guid clientId, LaserGameStatusDTO status)
    {
        try
        {
            if (status.PlayerScores.Count == 0)
            {
                // A false start or timing glitch can end a game immediately with nobody in it.
                // Recording those would fill the leaderboard with empty games.
                _logger.LogInformation("Skipped recording a completed game for client {ClientId} - no player scores", clientId);

                return Result<bool>.Ok(false);
            }

            GameResult gameResult = new()
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                PlayedAtUtc = DateTime.UtcNow,
                DurationSeconds = status.TotalTimeSeconds,
                PlayerScores = [.. status.PlayerScores.Select(x => new GameResultPlayerScore
                {
                    Id = Guid.NewGuid(),
                    GunId = x.GunId,
                    PlayerName = null,
                    Score = x.Score,
                    Accuracy = x.Accuracy,
                    Team = x.Team
                })]
            };

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.GameResults.Add(gameResult);

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Recorded completed game {GameResultId} for client {ClientId} with {PlayerCount} player(s)", gameResult.Id, clientId, gameResult.PlayerScores.Count);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record completed game for client {ClientId}", clientId);

            return Result<bool>.Fail($"Failed to record the completed game: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopScoresAsync(HallOfFamePeriod period, int take, Guid? clientId)
    {
        return await GetTopEntriesAsync(period, take, clientId, orderByAccuracy: false);
    }

    public async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopAccuracyAsync(HallOfFamePeriod period, int take, Guid? clientId)
    {
        return await GetTopEntriesAsync(period, take, clientId, orderByAccuracy: true);
    }

    public async Task<Result<IReadOnlyList<HallOfFameTeamTotalDTO>>> GetTopTeamTotalsAsync(HallOfFamePeriod period, Guid? clientId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<HallOfFameTeamTotalDTO> totals = await BuildScoreQuery(ctx, period, clientId)
                .Where(x => x.Team != null)
                .GroupBy(x => x.Team!.Value)
                .Select(g => new HallOfFameTeamTotalDTO
                {
                    Team = g.Key,
                    TotalScore = g.Sum(x => x.Score),
                    GameCount = g.Select(x => x.GameResultId).Distinct().Count()
                })
                .OrderByDescending(x => x.TotalScore)
                .ToListAsync();

            return Result<IReadOnlyList<HallOfFameTeamTotalDTO>>.Ok(totals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Hall of Fame team totals for period {Period}", period);

            return Result<IReadOnlyList<HallOfFameTeamTotalDTO>>.Fail($"Failed to load team totals: {ex.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopEntriesAsync(HallOfFamePeriod period, int take, Guid? clientId, bool orderByAccuracy)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<GameResultPlayerScore> query = BuildScoreQuery(ctx, period, clientId);

            // Ties break on the other metric, then on recency, so the board is stable between
            // refreshes rather than reshuffling equal rows on every poll.
            IOrderedQueryable<GameResultPlayerScore> orderedQuery = orderByAccuracy
                ? query.OrderByDescending(x => x.Accuracy).ThenByDescending(x => x.Score)
                : query.OrderByDescending(x => x.Score).ThenByDescending(x => x.Accuracy);

            List<HallOfFameEntryDTO> entries = await orderedQuery
                .ThenByDescending(x => x.GameResult!.PlayedAtUtc)
                .Take(take)
                .Select(x => new HallOfFameEntryDTO
                {
                    GunId = x.GunId,
                    PlayerName = x.PlayerName,
                    Score = x.Score,
                    Accuracy = x.Accuracy,
                    Team = x.Team,
                    PlayedAtUtc = x.GameResult!.PlayedAtUtc
                })
                .ToListAsync();

            return Result<IReadOnlyList<HallOfFameEntryDTO>>.Ok(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Hall of Fame entries for period {Period}", period);

            return Result<IReadOnlyList<HallOfFameEntryDTO>>.Fail($"Failed to load leaderboard entries: {ex.Message}");
        }
    }

    private static IQueryable<GameResultPlayerScore> BuildScoreQuery(ApplicationDbContext ctx, HallOfFamePeriod period, Guid? clientId)
    {
        DateTime? fromUtc = period.ToUtcLowerBound(DateTime.Now);

        IQueryable<GameResultPlayerScore> query = ctx.GameResultPlayerScores
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.GameResult);

        if (fromUtc.HasValue)
        {
            query = query.Where(x => x.GameResult!.PlayedAtUtc >= fromUtc.Value);
        }

        // A null clientId is venue-wide. Clients are not location-scoped (Client has no
        // LocationId), so there is no per-location variant of this.
        if (clientId.HasValue)
        {
            query = query.Where(x => x.GameResult!.ClientId == clientId.Value);
        }

        return query;
    }
}
