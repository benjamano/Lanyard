using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

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
// the file is written, so the day rows always add up to the total exactly. Minutes is clocked
// time; PaidMinutes is clocked time less BreakDeductedMinutes under the company's break rule.
public record TimesheetExportRow(
    TimesheetExportRowType Type,
    string Employee,
    DateOnly? Date,
    TimeOnly? FirstIn,
    TimeOnly? LastOut,
    int Entries,
    int? Minutes,
    int? RotaBreakMinutes,
    TimesheetExclusionReason? ExclusionReason,
    int? BreakDeductedMinutes = null,
    int? PaidMinutes = null);

// The company's break rule as the export applies it - built once from CompanySchedulingSettings so
// the export rules don't depend on the settings entity.
public record BreakDeductionPolicy(BreakDeductionRule Rule, int Minutes, int AfterMinutes)
{
    public static readonly BreakDeductionPolicy None = new(BreakDeductionRule.None, 0, 0);

    public static BreakDeductionPolicy From(CompanySchedulingSettings settings) => settings.BreakDeduction switch
    {
        BreakDeductionRule.RotaBreak => new(BreakDeductionRule.RotaBreak, 0, 0),
        BreakDeductionRule.FixedOverThreshold => new(
            BreakDeductionRule.FixedOverThreshold,
            settings.BreakDeductionMinutes,
            (int)Math.Round(settings.BreakDeductionAfterHours * 60m, MidpointRounding.AwayFromZero)),
        _ => None
    };

    // "20 min is taken off any day over 6 h", for the export dialog.
    public string Describe() => Rule switch
    {
        BreakDeductionRule.RotaBreak => "The rota break of each shift is taken off paid time.",
        BreakDeductionRule.FixedOverThreshold =>
            $"{Minutes} min is taken off paid time on any day over {(AfterMinutes / 60m).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} h.",
        _ => "Breaks aren't taken off - paid time is the time on the clock."
    };
}

public record PayPeriod(DateOnly From, DateOnly To);

// The pay periods the export dialog offers in one click. IsConfigured is false for a weekly-based
// frequency with no anchor date yet, when the periods are only a guess.
public record PayPeriodPresets(PayPeriod Current, PayPeriod Previous, string Description, bool IsConfigured, BreakDeductionPolicy Breaks);

public record TimesheetExportSummary(
    string LocationName,
    DateOnly From,
    DateOnly To,
    int People,
    int DayRows,
    int ApprovedMinutes,
    int AwaitingApproval,
    int NeedsReview,
    int StillClockedIn,
    int BreakDeductedMinutes = 0,
    int PaidMinutes = 0)
{
    public int ExcludedCount => AwaitingApproval + NeedsReview + StillClockedIn;
}

public record TimesheetExport(TimesheetExportSummary Summary, List<TimesheetExportRow> Rows);

// Built in memory and handed straight to the browser - never saved to disk or the File Manager,
// because it holds staff names and hours.
public record TimesheetExportFile(string FileName, string ContentType, byte[] Content, TimesheetExportSummary Summary);
