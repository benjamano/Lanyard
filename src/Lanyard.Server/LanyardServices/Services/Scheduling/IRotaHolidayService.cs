using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// A location's school holidays, bank holidays and closed days (RotaHoliday), which Auto-build reads
// to choose which shift templates run. Managed by the people who build that location's rota
// (SchedulingAccess.CanManageLocation).
public interface IRotaHolidayService
{
    // The location's holidays ending on or after `from` (all of them when null), earliest first.
    Task<Result<List<RotaHoliday>>> GetHolidaysAsync(LocationScope scope, int locationId, DateOnly? from = null);

    // Creates (Id empty) or updates a holiday.
    Task<Result<RotaHoliday>> SaveHolidayAsync(LocationScope scope, RotaHoliday holiday, string actingUserId);

    // Takes a holiday off the rota (IsActive = false). Shifts already made are left alone.
    Task<Result<bool>> RemoveHolidayAsync(LocationScope scope, Guid holidayId, string actingUserId);

    // England and Wales bank holidays from..to that the location doesn't already have a bank
    // holiday or closed day on, as they'd be added (staffed like a Sunday).
    Task<Result<List<RotaHoliday>>> GetMissingBankHolidaysAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to);

    // Adds GetMissingBankHolidaysAsync's days. Returns how many were added.
    Task<Result<int>> AddBankHolidaysAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId);
}
