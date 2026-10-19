using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// The weekly pattern of when someone can't, or would rather, work (StaffAvailability). A person
// edits their own from My Shifts; a manager can edit anyone they manage from the user editor.
// Nobody is notified about a change - the rota builder reads it when it next needs it.
public interface IStaffAvailabilityService
{
    // The person's active windows, Monday first. Their own, or someone a Manager/Admin manages.
    Task<Result<List<StaffAvailability>>> GetAsync(LocationScope scope, string userId, string actingUserId);

    // Creates (Id empty) or updates one window for row.UserId. Refused if it overlaps another of
    // their windows while both are in effect.
    Task<Result<StaffAvailability>> SaveAsync(LocationScope scope, StaffAvailability row, string actingUserId);

    Task<Result<bool>> RemoveAsync(LocationScope scope, Guid id, string actingUserId);
}
