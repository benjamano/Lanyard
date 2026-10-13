using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
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
    // any positions always has exactly one primary. Any new position's required training they
    // haven't done is then assigned to them (IRequiredTrainingService); that never fails the save.
    Task<Result<UserPositionsSaved>> SetUserPositionsAsync(LocationScope scope, string userId, List<Guid> positionIds, Guid? primaryPositionId, string actingUserId);

    // The training a position needs before someone can pick up its shifts, with each Course (and
    // its Location) loaded. Includes archived courses, which are kept but no longer enforced.
    Task<Result<List<StaffPositionRequiredCourse>>> GetRequiredCoursesAsync(LocationScope scope, Guid positionId);

    // The names of the (active) courses each of the company's positions requires, for showing
    // on the positions list. Positions that require nothing are left out.
    Task<Result<Dictionary<Guid, List<string>>>> GetRequiredCourseNamesAsync(LocationScope scope, int companyId);

    // Sets which courses the position requires. Only courses the caller can see are added or
    // removed - a Manager leaves another location's (unshared) requirements as they are. A newly
    // required course is assigned to the position's holders who haven't done it; that never
    // fails the save.
    Task<Result<RequiredTrainingAssignResult>> SetRequiredCoursesAsync(LocationScope scope, Guid positionId, List<Guid> courseIds, string actingUserId);

    // How many of the position's holders are up to date on these courses today - courseIds is
    // what's ticked, saved or not. A Manager sees their own location's staff.
    Task<Result<TrainingCoverage>> GetTrainingCoverageAsync(LocationScope scope, Guid positionId, List<Guid> courseIds);

    // Bulk lookup for the rota builder's staff rows - one query for a whole location's members.
    // A user with no positions maps to null.
    Task<Result<Dictionary<string, UserPosition?>>> GetPrimaryPositionsForUsersAsync(IEnumerable<string> userIds);
}
