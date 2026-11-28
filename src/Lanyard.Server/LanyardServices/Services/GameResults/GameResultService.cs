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

    public async Task<Result<bool>> RecordCompletedGameAsync(Guid clientId, LaserGameStatusDTO status, DateTime? startedAtUtc = null)
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

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Kiosk hub calls run as the system tenant, which would otherwise stamp every game as
            // Play2Day. The game belongs to whichever company owns the kiosk that played it.
            int? companyId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync();

            if (companyId is null)
            {
                _logger.LogWarning("Dropped a completed game for client {ClientId} - client not found", clientId);

                return Result<bool>.Fail("Cannot record the completed game: the client was not found.");
            }

            DateTime playedAtUtc = DateTime.UtcNow;

            GameResult gameResult = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId.Value,
                ClientId = clientId,
                PlayedAtUtc = playedAtUtc,
                // A start after the end can only be a clock glitch; drop it and let readers estimate.
                StartedAtUtc = startedAtUtc is { } start && start <= playedAtUtc ? start : null,
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

            ctx.GameResults.Add(gameResult);

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Recorded completed game {GameResultId} for client {ClientId} in company {CompanyId} with {PlayerCount} player(s)", gameResult.Id, clientId, gameResult.CompanyId, gameResult.PlayerScores.Count);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record completed game for client {ClientId}", clientId);

            return Result<bool>.Fail($"Failed to record the completed game: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<RecentGameDTO>>> GetRecentGamesAsync(Guid clientId, int take, int? companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<GameResult> query = ctx.GameResults
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.PlayerScores)
                .Where(x => x.ClientId == clientId);

            // Kiosk displays run as the system tenant, so scope to the dashboard's company here.
            if (companyId.HasValue)
            {
                query = query.Where(x => x.CompanyId == companyId.Value);
            }

            List<GameResult> games = await query
                .OrderByDescending(x => x.PlayedAtUtc)
                .Take(Math.Max(1, take))
                .ToListAsync();

            List<RecentGameDTO> recentGames = [.. games.Select(ToRecentGame)];

            return Result<IReadOnlyList<RecentGameDTO>>.Ok(recentGames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recent games for client {ClientId}", clientId);

            return Result<IReadOnlyList<RecentGameDTO>>.Fail($"Failed to load recent games: {ex.Message}");
        }
    }

    // Static and public so the winner rules can be tested without a database.
    public static RecentGameDTO ToRecentGame(GameResult game)
    {
        RecentGameDTO dto = new()
        {
            Id = game.Id,
            EndedAtUtc = game.PlayedAtUtc,
            StartedAtUtc = game.StartedAtUtc ?? game.PlayedAtUtc.AddSeconds(-game.DurationSeconds),
            StartIsEstimate = game.StartedAtUtc is null,
            PlayerCount = game.PlayerScores.Count,
            TeamTotals = [.. game.PlayerScores
                .Where(x => x.Team != null)
                .GroupBy(x => x.Team!.Value)
                .Select(g => new RecentGameTeamTotalDTO { Team = g.Key, TotalScore = g.Sum(x => x.Score) })
                .OrderByDescending(x => x.TotalScore)
                .ThenBy(x => x.Team)]
        };

        if (dto.TeamTotals.Count > 0)
        {
            RecentGameTeamTotalDTO top = dto.TeamTotals[0];

            dto.WinningScore = top.TotalScore;
            dto.IsDraw = dto.TeamTotals.Count(x => x.TotalScore == top.TotalScore) > 1;
            dto.WinningTeam = dto.IsDraw ? null : top.Team;
        }
        else if (game.PlayerScores.Count > 0)
        {
            // Free-for-all: nobody had a team, so the best gun wins.
            int topScore = game.PlayerScores.Max(x => x.Score);
            List<GameResultPlayerScore> topGuns = [.. game.PlayerScores.Where(x => x.Score == topScore)];

            dto.WinningScore = topScore;
            dto.IsDraw = topGuns.Count > 1;
            dto.WinningGunId = dto.IsDraw ? null : topGuns[0].GunId;
        }

        return dto;
    }

    public async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopScoresAsync(HallOfFamePeriod period, int take, Guid? clientId, int? companyId)
    {
        return await GetTopEntriesAsync(period, take, clientId, companyId, orderByAccuracy: false);
    }

    public async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopAccuracyAsync(HallOfFamePeriod period, int take, Guid? clientId, int? companyId)
    {
        return await GetTopEntriesAsync(period, take, clientId, companyId, orderByAccuracy: true);
    }

    public async Task<Result<IReadOnlyList<HallOfFameTeamTotalDTO>>> GetTopTeamTotalsAsync(HallOfFamePeriod period, Guid? clientId, int? companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<HallOfFameTeamTotalDTO> totals = await BuildScoreQuery(ctx, period, clientId, companyId)
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

    private async Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopEntriesAsync(HallOfFamePeriod period, int take, Guid? clientId, int? companyId, bool orderByAccuracy)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<GameResultPlayerScore> query = BuildScoreQuery(ctx, period, clientId, companyId);

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

    private static IQueryable<GameResultPlayerScore> BuildScoreQuery(ApplicationDbContext ctx, HallOfFamePeriod period, Guid? clientId, int? companyId)
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

        // The tenant filter already does this for signed-in staff, but kiosk displays run as the
        // system tenant and see every company's games unless the caller scopes them here.
        if (companyId.HasValue)
        {
            query = query.Where(x => x.GameResult!.CompanyId == companyId.Value);
        }

        return query;
    }
}
