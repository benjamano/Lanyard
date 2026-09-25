using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface IStaffPositionService
{
    Task<Result<List<StaffPosition>>> GetPositionsAsync(int companyId, bool includeInactive = false);
    Task<Result<StaffPosition>> SavePositionAsync(LocationScope scope, StaffPosition position);
    Task<Result<bool>> DeactivatePositionAsync(LocationScope scope, Guid positionId);

    Task<Result<List<UserPosition>>> GetUserPositionsAsync(string userId);

    // Replaces the user's whole set of positions. primaryPositionId must be one of positionIds;
    // when null and the set is non-empty the first position becomes primary, so a user with
    // any positions always has exactly one primary.
    Task<Result<List<UserPosition>>> SetUserPositionsAsync(LocationScope scope, string userId, List<Guid> positionIds, Guid? primaryPositionId);

    // Bulk lookup for the rota builder's staff rows - one query for a whole location's members.
    // A user with no positions maps to null.
    Task<Result<Dictionary<string, UserPosition?>>> GetPrimaryPositionsForUsersAsync(IEnumerable<string> userIds);
}
