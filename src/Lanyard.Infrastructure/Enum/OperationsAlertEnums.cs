namespace Lanyard.Infrastructure.Enum;

// What an operations alert is about. Stored as its int value, so never renumber a member.
// 8 and above are kept for producers still to come (policies, rota holidays, DMX output faults).
public enum OperationsAlertType
{
    KioskOffline = 1,
    KioskNeedsAttention = 2,
    AutomationRuleFailed = 3,
    MissedClockIn = 4,
    TrainingOverdue = 5,
    StaffDocumentExpiring = 6,
    StaffDocumentExpired = 7
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
