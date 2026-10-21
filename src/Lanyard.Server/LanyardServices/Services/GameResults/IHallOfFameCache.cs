using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IHallOfFameCache
{
    // Raised with the company whose games changed, after its cached leaderboards are dropped, so
    // widgets can reload straight away instead of waiting for a poll.
    event Action<int>? Changed;

    // Returns the cached snapshot for this exact window, or runs load once and shares the result
    // with every caller asking for the same key meanwhile. A load that throws is not cached.
    // companyId must be the company the rows were filtered to, never a caller-supplied value
    // that the tenant filter might have overridden - otherwise one company could read another's.
    Task<HallOfFameSnapshotDTO> GetOrLoadAsync(int companyId, Guid? clientId, DateTime? fromUtc, int take, Func<Task<HallOfFameSnapshotDTO>> load);

    // Drops every cached leaderboard for the company (all kiosks, periods and sizes) and raises
    // Changed. Call after any write to its game results.
    void Invalidate(int companyId);
}
