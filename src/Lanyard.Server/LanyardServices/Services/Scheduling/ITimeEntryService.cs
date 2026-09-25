using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface ITimeEntryService
{
    // Everyone with a published shift at the terminal's location today that can still be clocked
    // for, plus anyone currently clocked in there - the names people tap before typing their PIN.
    Task<Result<List<TerminalRosterEntry>>> GetTerminalRosterAsync(Guid terminalId);

    // Clock in if not on the clock, out if they are. Clocking in is only allowed within the
    // company's clock-in window around one of the person's published shifts here; clocking out
    // is always allowed.
    Task<Result<ClockActionResult>> ClockByPinAsync(Guid terminalId, string userId, string pin);
    Task<Result<ClockActionResult>> ClockByQrAsync(string nonce, string userId);

    Task<Result<ClockState>> GetClockStateAsync(string userId);

    // A location's clocked entries and published shifts for a date range, paired up per person,
    // including shifts nobody clocked for.
    Task<Result<TimesheetView>> GetTimesheetAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to);

    Task<Result<List<TimeEntry>>> GetEntriesForUserAsync(string userId, DateOnly from, DateOnly to);

    // A manager adding or correcting an entry. Saving counts as the manager's approval of a
    // finished entry, and clears any review flag.
    Task<Result<TimeEntry>> SaveEntryAsync(LocationScope scope, TimeEntry entry, string actingUserId);

    Task<Result<bool>> ApproveEntryAsync(LocationScope scope, Guid entryId, string approverUserId);

    // Approves every finished, unflagged entry in the range; entries flagged for review are left
    // for a manager to look at individually.
    Task<Result<int>> ApproveAllAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string approverUserId);

    Task<Result<bool>> DeactivateEntryAsync(LocationScope scope, Guid entryId, string actingUserId);

    // Everyone on the published rota at the location today (one line per shift, in start order)
    // plus anyone on the clock there with no shift today. For the "Who's on today" widget, which
    // decides for itself what a signed-out screen may show.
    Task<Result<List<DayBoardEntry>>> GetDayBoardAsync(int locationId);
}
