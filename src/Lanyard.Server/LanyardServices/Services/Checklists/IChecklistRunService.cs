using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;

namespace Lanyard.Application.Services.Checklists;

// Ticking checklists and seeing how they went. Staff use the "for me" methods on their phone; the
// manager methods are for the location's Managers and its company's Admins
// (SchedulingAccess.CanManageLocation). Only today's tasks can be ticked or unticked.
public interface IChecklistRunService
{
    // Today's checklists where the person is working: the location they're clocked in at, or else the
    // one they signed in to. They must belong to it (Admins excepted).
    Task<Result<ChecklistDayView>> GetMyTodayAsync(string userId, LocationScope scope);

    // Ticks a task as the signed-in person. A number task needs its reading; a note is only kept on a
    // tick-with-note task.
    Task<Result<bool>> CompleteAsync(string userId, LocationScope scope, Guid runItemId, string? note, decimal? reading);

    // Takes a tick back: the person who ticked it, or a Manager/Admin of the location, on the same day.
    Task<Result<bool>> UndoAsync(string userId, LocationScope scope, Guid completionId);

    // A location's checklists on one day, today or earlier, with who ticked what.
    Task<Result<ChecklistDayView>> GetDayBoardAsync(LocationScope scope, string userId, int locationId, DateOnly day);

    // One row per checklist per day between the dates (at most 62 days, nothing after today), newest
    // first. Days nobody touched show as missed.
    Task<Result<List<ChecklistHistoryRow>>> GetHistoryAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to);
}
