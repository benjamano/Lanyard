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

    // The clock-in terminal. It's anonymous (the system tenant), so every call is checked against the
    // terminal itself: only its own location's checklists, and only the person whose PIN started the
    // grant can tick. A grant lapses two minutes after its last use.

    // Today's checklists at the terminal's location; CanTick is for the grant's person, if any.
    Task<Result<ChecklistDayView>> GetTerminalDayAsync(Guid terminalId, string? grantToken);

    // Who can be picked to tick: anyone with a published shift at the terminal's location today or
    // clocked in there now, and its Managers.
    Task<Result<List<TerminalChecklistPerson>>> GetTerminalPeopleAsync(Guid terminalId);

    // Checks the person's PIN (the same lockout as clocking in, without clocking) and returns a grant.
    Task<Result<string>> StartTerminalTickingAsync(Guid terminalId, string userId, string pin);

    Task<Result<bool>> CompleteOnTerminalAsync(Guid terminalId, string? grantToken, Guid runItemId, string? note, decimal? reading);

    Task<Result<bool>> UndoOnTerminalAsync(Guid terminalId, string? grantToken, Guid completionId);

    void EndTerminalTicking(string? grantToken);

    // The Checklist card on a dashboard. Kiosk dashboards run signed out, so each call names the
    // dashboard's company (for reading) or the kiosk and its viewer token (for ticking).

    // Today's run of one checklist of that company, or Ok(null) when it doesn't run today. Read-only:
    // CanTick is false.
    Task<Result<ChecklistRunView?>> GetCardAsync(Guid checklistId, int companyId);

    // A tick made on a kiosk, recorded as the kiosk (no person). Needs the kiosk's viewer token and a
    // Checklist card in its company that allows kiosk ticking for that checklist.
    Task<Result<bool>> CompleteOnKioskAsync(Guid clientId, string? viewerToken, Guid runItemId, string? note, decimal? reading);

    // Whether this kiosk may tick this checklist right now (its token is good, same company, and a card
    // allows kiosk ticking), so the card can show read-only rather than fail on the first tick.
    Task<Result<bool>> CanTickOnKioskAsync(Guid clientId, string? viewerToken, Guid checklistId);

    // Takes back a kiosk tick made today (not a person's).
    Task<Result<bool>> UndoOnKioskAsync(Guid clientId, string? viewerToken, Guid completionId);
}
