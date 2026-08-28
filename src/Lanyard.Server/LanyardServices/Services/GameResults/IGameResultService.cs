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
    /// </remarks>
    Task<Result<bool>> RecordCompletedGameAsync(Guid clientId, LaserGameStatusDTO status);

    Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopScoresAsync(HallOfFamePeriod period, int take, Guid? clientId);

    Task<Result<IReadOnlyList<HallOfFameEntryDTO>>> GetTopAccuracyAsync(HallOfFamePeriod period, int take, Guid? clientId);

    Task<Result<IReadOnlyList<HallOfFameTeamTotalDTO>>> GetTopTeamTotalsAsync(HallOfFamePeriod period, Guid? clientId);
}
