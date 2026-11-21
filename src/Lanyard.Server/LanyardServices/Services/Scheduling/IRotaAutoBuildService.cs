using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Auto-build: turns a location's shift templates into draft shifts for some dates and picks who
// works each one (RotaAutoBuildRules). Shifts already on the rota are never changed; anything
// nobody can work is made as an open shift. Nothing is published - the manager reviews and
// publishes as usual.
public interface IRotaAutoBuildService
{
    // What Auto-build would add, without saving anything.
    Task<Result<AutoBuildResult>> PreviewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to);

    // Works it out again from the rota as it is now and saves the shifts as drafts.
    Task<Result<AutoBuildResult>> ApplyAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId);
}
