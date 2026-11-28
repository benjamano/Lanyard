using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface IGameResultService
{
    /// <summary>
    /// Persists one finished game and its final per-gun scores.
    /// </summary>
    /// <remarks>
    /// Callers must only invoke this on the InGame -> NotStarted edge. The kiosk keeps
    /// re-publishing the final scores for the whole idle period afterwards (GameStateService only
    /// clears them on the next game start), so calling this on every NotStarted heartbeat would
    /// write a duplicate row each time.
    ///
    /// The game is stored under the client's company. If the client can't be found (or isn't
    /// visible to the caller's company) nothing is written and a failed result is returned.
    /// </remarks>
    // startedAtUtc is when the server saw the game go InGame; null if it missed the start.
    Task<Result<bool>> RecordCompletedGameAsync(Guid clientId, LaserGameStatusDTO status, DateTime? startedAtUtc = null);

    // The kiosk's most recent finished games, newest first, with each game's winner worked out.
    // companyId works as it does for the Hall of Fame queries below.
    Task<Result<IReadOnlyList<RecentGameDTO>>> GetRecentGamesAsync(Guid clientId, int take, int? companyId);

    // companyId limits results to one company's games. Kiosk displays run unfiltered (system
    // tenant), so they must pass the dashboard's company or they would see every company's games.
    // Null leaves it to the caller's tenant filter.
    Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopScoresAsync(HallOfFamePeriod period, int take, Guid? clientId, int? companyId);

    Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopAccuracyAsync(HallOfFamePeriod period, int take, Guid? clientId, int? companyId);

    Task<Result<IReadOnlyList<HallOfFameTeamTotalDTO>>> GetTopTeamTotalsAsync(HallOfFamePeriod period, Guid? clientId, int? companyId);
}
