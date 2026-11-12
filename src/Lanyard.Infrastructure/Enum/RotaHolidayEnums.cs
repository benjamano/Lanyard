namespace Lanyard.Infrastructure.Enum;

// What kind of day a manager has marked on a location's rota (RotaHoliday). Stored as an int.
public enum RotaHolidayKind
{
    // Local school holidays: shift templates set to "School holidays only" run, "Term time only"
    // ones don't.
    SchoolHoliday = 0,

    // A bank holiday: staffed like another day of the week (Sunday unless the manager picks one),
    // and counted as a school holiday because schools are shut.
    BankHoliday = 1,

    // The venue is shut: Auto-build makes no template shifts at all.
    Closed = 2
}

// Which weeks a shift template runs in, judged against the location's school holidays. Stored as an int.
public enum TemplateRuns
{
    EveryWeek = 0,
    TermTimeOnly = 1,
    SchoolHolidaysOnly = 2
}

public static class RotaHolidayEnumExtensions
{
    public static string Label(this RotaHolidayKind kind) => kind switch
    {
        RotaHolidayKind.SchoolHoliday => "School holiday",
        RotaHolidayKind.BankHoliday => "Bank holiday",
        RotaHolidayKind.Closed => "Closed",
        _ => kind.ToString()
    };

    public static string Label(this TemplateRuns runs) => runs switch
    {
        TemplateRuns.EveryWeek => "Every week",
        TemplateRuns.TermTimeOnly => "Term time only",
        TemplateRuns.SchoolHolidaysOnly => "School holidays only",
        _ => runs.ToString()
    };
}
