namespace Lanyard.Application.Services.Scheduling;

// England and Wales bank holidays worked out from the usual rules, for the "Add bank holidays"
// button on the Holidays page. One-off days the government adds (a coronation, a jubilee) and the
// odd year a holiday is moved aren't known, so managers check them and add those by hand.
// Scotland and Northern Ireland have different days.
public static class UkBankHolidays
{
    public static List<(DateOnly Date, string Name)> EnglandAndWales(int year)
    {
        List<(DateOnly, string)> days = [];

        DateOnly newYear = new(year, 1, 1);
        days.Add((NextWeekday(newYear), newYear.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "New Year's Day (substitute day)" : "New Year's Day"));

        DateOnly easter = EasterSunday(year);
        days.Add((easter.AddDays(-2), "Good Friday"));
        days.Add((easter.AddDays(1), "Easter Monday"));

        days.Add((FirstMonday(year, 5), "Early May bank holiday"));
        days.Add((LastMonday(year, 5), "Spring bank holiday"));
        days.Add((LastMonday(year, 8), "Summer bank holiday"));

        // Christmas and Boxing Day: a day falling at the weekend moves to the next free weekday.
        DateOnly christmas = new(year, 12, 25);
        DateOnly boxing = new(year, 12, 26);

        switch (christmas.DayOfWeek)
        {
            case DayOfWeek.Friday:
                days.Add((christmas, "Christmas Day"));
                days.Add((new DateOnly(year, 12, 28), "Boxing Day (substitute day)"));
                break;
            case DayOfWeek.Saturday:
                days.Add((new DateOnly(year, 12, 27), "Christmas Day (substitute day)"));
                days.Add((new DateOnly(year, 12, 28), "Boxing Day (substitute day)"));
                break;
            case DayOfWeek.Sunday:
                days.Add((boxing, "Boxing Day"));
                days.Add((new DateOnly(year, 12, 27), "Christmas Day (substitute day)"));
                break;
            default:
                days.Add((christmas, "Christmas Day"));
                days.Add((boxing, "Boxing Day"));
                break;
        }

        return days.OrderBy(x => x.Item1).ToList();
    }

    // Anonymous Gregorian algorithm (Meeus/Jones/Butcher).
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    private static DateOnly NextWeekday(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Saturday => day.AddDays(2),
        DayOfWeek.Sunday => day.AddDays(1),
        _ => day
    };

    private static DateOnly FirstMonday(int year, int month)
    {
        DateOnly day = new(year, month, 1);
        return day.AddDays(((int)DayOfWeek.Monday - (int)day.DayOfWeek + 7) % 7);
    }

    private static DateOnly LastMonday(int year, int month)
    {
        DateOnly day = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        return day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
    }
}
