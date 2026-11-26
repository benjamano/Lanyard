namespace Lanyard.Infrastructure.Enum;

// What an operations alert is about. Stored as its int value, so never renumber a member.
// Some numbers are kept for producers still to come; see the comments between members.
public enum OperationsAlertType
{
    KioskOffline = 1,
    KioskNeedsAttention = 2,
    AutomationRuleFailed = 3,
    MissedClockIn = 4,
    TrainingOverdue = 5,
    StaffDocumentExpiring = 6,
    StaffDocumentExpired = 7,

    // The Just Step Online booking import couldn't run (bad credentials, site down, parse failure),
    // raised by JustStepImportHostedService / BookingImportService.
    PartyBookingImportFailed = 8,

    // 9-11 are kept for the producers named above (policies, rota holidays, DMX output faults).

    // A location checklist went past its due time (or its day ended) with tasks left, raised by
    // ChecklistMissedAlertHostedService and cleared if it's finished late.
    ChecklistMissed = 12,

    // 13 is kept for checklist readings out of range (item 10.2, F-Checklist-Reading-Ranges).

    // A party was booked, moved, changed or cancelled after its day's rota was built, so that day's
    // party staffing no longer matches. One per location and day, raised by PartyStaffingWatch and
    // cleared once Auto-build or a rota edit brings the day back into line.
    PartyStaffingOutOfDate = 14
}

// Higher is more urgent, so "at least Warning" is a plain comparison. Always shown with its text
// label and an icon, never by colour alone.
public enum OperationsAlertSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

public enum OperationsAlertStatus
{
    Open = 1,
    Acknowledged = 2,
    Resolved = 3
}

// Auto: the condition cleared by itself (the kiosk came back, the rule ran cleanly). Manual: a
// person resolved it from the alerts page or widget.
public enum OperationsAlertResolution
{
    Auto = 1,
    Manual = 2
}
