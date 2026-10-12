namespace Lanyard.Infrastructure.DTO.Scheduling;

public enum TimesheetExportRowType
{
    // One person's approved hours for one venue-local day.
    Day = 0,

    // A clocked entry left out of the hours, listed so payroll can see it was not silently dropped.
    Excluded = 1,

    // One person's approved hours for the whole range.
    Total = 2
}

public enum TimesheetExclusionReason
{
    AwaitingApproval = 0,
    NeedsReview = 1,
    StillClockedIn = 2
}

// One row of the payroll export. Minutes are the source of truth: hours are derived from them when
// the file is written, so the day rows always add up to the total exactly.
public record TimesheetExportRow(
    TimesheetExportRowType Type,
    string Employee,
    DateOnly? Date,
    TimeOnly? FirstIn,
    TimeOnly? LastOut,
    int Entries,
    int? Minutes,
    int? RotaBreakMinutes,
    TimesheetExclusionReason? ExclusionReason);

public record TimesheetExportSummary(
    string LocationName,
    DateOnly From,
    DateOnly To,
    int People,
    int DayRows,
    int ApprovedMinutes,
    int AwaitingApproval,
    int NeedsReview,
    int StillClockedIn)
{
    public int ExcludedCount => AwaitingApproval + NeedsReview + StillClockedIn;
}

public record TimesheetExport(TimesheetExportSummary Summary, List<TimesheetExportRow> Rows);

// Built in memory and handed straight to the browser - never saved to disk or the File Manager,
// because it holds staff names and hours.
public record TimesheetExportFile(string FileName, string ContentType, byte[] Content, TimesheetExportSummary Summary);
