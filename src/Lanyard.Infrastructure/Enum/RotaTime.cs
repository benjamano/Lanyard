namespace Lanyard.Infrastructure.Enum;

// Every scheduling instant is stored in UTC (Npgsql maps DateTime to timestamptz and rejects
// non-UTC writes), but a rota is read and written in venue-local wall-clock time: a 09:00 shift
// is 09:00 in both January and July. The production container runs in UTC with no TZ set, so
// DateTime.Now / ToLocalTime() would put every summer shift an hour late - the venue's zone is
// therefore named explicitly here rather than taken from the server.
//
// Deliberately NOT the same basis as HallOfFamePeriodExtensions/Client.AutoRestartTimeOfDay,
// which use server-local time; those predate this and only happen to be right when TZ is set.
public static class RotaTime
{
    public static readonly TimeZoneInfo VenueZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static DateTime ToVenueLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), VenueZone);

    public static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(ToVenueLocal(utc));

    public static TimeOnly LocalTime(DateTime utc) => TimeOnly.FromDateTime(ToVenueLocal(utc));

    // A wall-clock time that doesn't exist (the hour skipped when clocks go forward in March) is
    // moved past the gap rather than throwing; an ambiguous one (the repeated hour in October)
    // resolves to standard time, which is TimeZoneInfo's own default.
    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (VenueZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, VenueZone);
    }

    public static DateTime StartOfDayUtc(DateOnly date) => ToUtc(date, TimeOnly.MinValue);

    // Monday-start, same convention as the Hall of Fame "this week" window.
    public static DateOnly GetWeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly Today(DateTime utcNow) => LocalDate(utcNow);
}
