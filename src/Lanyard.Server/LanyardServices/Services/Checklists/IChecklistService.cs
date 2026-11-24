using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Checklists;

// Setting up a location's checklists ("Opening", "Closing") and their ordered tasks. Managed by the
// people who run that location's rota (SchedulingAccess.CanManageLocation): an Admin for any
// location in their company, a Manager for the location they signed in under. Changes only affect
// days whose run hasn't been made yet, plus today's run while nobody has ticked anything.
public interface IChecklistService
{
    // The location's active checklists (or every one, archived included), in SortOrder then name,
    // each with its active tasks in order and its position loaded.
    Task<Result<List<Checklist>>> GetChecklistsAsync(LocationScope scope, int locationId, bool includeArchived = false);

    // One checklist with its active tasks in order.
    Task<Result<Checklist>> GetChecklistAsync(LocationScope scope, Guid checklistId);

    // Creates (Id empty) or updates a checklist's settings. Tasks are saved separately.
    Task<Result<Checklist>> SaveChecklistAsync(LocationScope scope, Checklist checklist, string actingUserId);

    // Archives (active = false) or restores a checklist. Its history stays.
    Task<Result<bool>> SetArchivedAsync(LocationScope scope, Guid checklistId, bool archived, string actingUserId);

    // Creates (Id empty, added at the end) or updates a task.
    Task<Result<ChecklistTask>> SaveTaskAsync(LocationScope scope, ChecklistTask task, string actingUserId);

    // Takes a task off the checklist (archived, so past days still show it).
    Task<Result<bool>> RemoveTaskAsync(LocationScope scope, Guid taskId, string actingUserId);

    // Moves a task one place up (-1) or down (+1).
    Task<Result<bool>> MoveTaskAsync(LocationScope scope, Guid taskId, int direction, string actingUserId);
}
