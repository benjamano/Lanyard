using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// The shifts a location needs every week (ShiftTemplate), which Auto-build on the rota turns into
// draft shifts. Managed by the same people who build that location's rota
// (SchedulingAccess.CanManageLocation); staff never see them.
public interface IShiftTemplateService
{
    // The location's active templates (or every one, archived included), Monday first then by
    // start time, each with its slots and their positions loaded.
    Task<Result<List<ShiftTemplate>>> GetTemplatesAsync(LocationScope scope, int locationId, bool includeArchived = false);

    // Creates (Id empty) or updates a template, replacing its slots with template.Slots.
    Task<Result<ShiftTemplate>> SaveTemplateAsync(LocationScope scope, ShiftTemplate template, string actingUserId);

    // The location's staff, with the positions each holds, for picking a template position's regulars.
    Task<Result<List<RegularCandidate>>> GetRegularCandidatesAsync(LocationScope scope, int locationId);

    // Archives (active = false) or restores a template. Shifts already made from it are untouched.
    Task<Result<bool>> SetArchivedAsync(LocationScope scope, Guid templateId, bool archived, string actingUserId);
}

// Someone who could be a template position's regular. Positions are the ones they hold.
public sealed record RegularCandidate(string UserId, string Name, IReadOnlySet<Guid> Positions);
