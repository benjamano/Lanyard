namespace Lanyard.Infrastructure.Enum;

// Whether, and how, unpaid breaks are taken off clocked time in the payroll export. Clocked time
// itself never has a break deducted (see TimeEntry.ClockedHours) - this only changes the export's
// paid columns. Stored as its int value, so never renumber an existing member.
public enum BreakDeductionRule
{
    // Paid time is clocked time.
    None = 0,

    // Take off the break written on the rota for the shift the time was clocked against.
    RotaBreak = 1,

    // Take off a fixed number of minutes from any day longer than a threshold - in the UK the
    // statutory minimum is a 20 minute break when working more than 6 hours.
    FixedOverThreshold = 2
}
