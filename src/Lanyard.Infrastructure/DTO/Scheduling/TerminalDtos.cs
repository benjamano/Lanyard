using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// What a paired tablet knows about itself once its cookie has been checked.
public record TerminalSession(Guid TerminalId, int LocationId, int CompanyId, string LocationName, string TerminalName);

// A pairing a manager has started on a tablet but not yet completed (see TerminalController).
public record PendingPairing(int LocationId, string TerminalName, string IssuedByUserId, bool SignOutAfterPairing, DateTime ExpiresUtc);

// The raw token exists only here, on its way into the tablet's cookie - it is never stored.
public record PairedTerminal(ClockInTerminal Terminal, string RawToken);

// One name on the terminal's "who's working today" list.
public record TerminalRosterEntry(string UserId, string DisplayName, string? ShiftSummary, DateTime? ShiftStartUtc, bool IsClockedIn, bool HasPin);

public record ClockActionResult(string GreetingName, ClockDirection Direction, DateTime AtUtc, string? ShiftSummary, decimal? ClockedHours);

public record ClockState(bool IsClockedIn, DateTime? SinceUtc, string? LocationName);

// Raised in-process when anyone clocks at a terminal, so the terminal's screen can greet a person
// who clocked in by scanning its QR code on their own phone.
public record TerminalClockEvent(Guid TerminalId, string GreetingName, ClockDirection Direction, DateTime AtUtc, ClockMethod Method);

// One line of a person's timesheet: a clocked entry, a published shift nobody clocked for, or both.
public record TimesheetLine(DateOnly Date, Shift? Shift, TimeEntry? Entry)
{
    public bool IsMissed(DateTime nowUtc) => Entry is null && Shift is not null && Shift.EndUtc < nowUtc;
}

public record TimesheetPerson(
    string UserId,
    string DisplayName,
    List<TimesheetLine> Lines,
    decimal ClockedHours,
    decimal ScheduledHours,
    int NeedsReviewCount,
    int AwaitingApprovalCount);

public record TimesheetView(int LocationId, string LocationName, DateOnly From, DateOnly To, List<TimesheetPerson> People)
{
    public int AwaitingApprovalCount => People.Sum(x => x.AwaitingApprovalCount);
}
