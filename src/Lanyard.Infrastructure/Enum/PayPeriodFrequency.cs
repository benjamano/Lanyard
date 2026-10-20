namespace Lanyard.Infrastructure.Enum;

// How often a company runs payroll, so the timesheet export can offer "last pay period" in one
// click. Stored as its int value in CompanySchedulingSettings.PayPeriod, so never renumber an
// existing member. Monthly is 0 so that settings rows saved before this existed read as monthly.
public enum PayPeriodFrequency
{
    // From a start day each month to the day before it the next month (1st = calendar month).
    Monthly = 0,

    // Seven, fourteen or twenty-eight days, counted from an anchor date that starts one period.
    Weekly = 1,
    Fortnightly = 2,
    FourWeekly = 3
}
