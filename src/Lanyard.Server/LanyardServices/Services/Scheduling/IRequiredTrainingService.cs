using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

public interface IRequiredTrainingService
{
    // Assigns each person the required training of the positions they hold (holdings) that they
    // haven't passed and aren't already on - so they can start it before their first shift.
    // onlyCourseIds limits it to some courses (a requirement that was just added). Sends the
    // usual "training assigned" email. Under a Manager's scope only their location's staff, and
    // courses they can see, are assigned; the rest are counted in CouldNotAssign.
    Task<Result<RequiredTrainingAssignResult>> AssignMissingAsync(
        LocationScope scope,
        string actingUserId,
        IReadOnlyCollection<(string UserId, Guid PositionId)> holdings,
        IReadOnlyCollection<Guid>? onlyCourseIds = null);
}
