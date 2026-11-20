namespace Lanyard.Application.Services.BookingImport;

// Decides, for the background sweep, whether a location is due an import: only inside its allowed
// hours (so it rests overnight) and only once its interval has passed since the last run. Pure and
// static so the decision can be unit-tested. "Run now" doesn't use this - a person asking for it
// always runs.
public static class BookingImportSchedule
{
    public const int DefaultFrequencyHours = 1;
    public const int DefaultActiveStartHour = 7;
    public const int DefaultActiveEndHour = 23;

    public const int MinFrequencyHours = 1;
    public const int MaxFrequencyHours = 24;

    // A little slack so an hourly job that last ran at 10:00:40 still fires on the ~11:00 tick even
    // if the timer is a few seconds early.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    // The window is [startHour, endHour): the hour the clock shows must be at or after the start and
    // before the end. Default 7..23 means 07:00-22:59.
    public static bool IsWithinActiveHours(int venueHour, int startHour, int endHour) =>
        venueHour >= startHour && venueHour < endHour;

    public static bool IsDue(DateTime nowUtc, DateTime venueLocalNow, DateTime? lastRunUtc, int frequencyHours, int startHour, int endHour)
    {
        if (!IsWithinActiveHours(venueLocalNow.Hour, startHour, endHour))
        {
            return false;
        }

        if (lastRunUtc is null)
        {
            return true;
        }

        int hours = Math.Clamp(frequencyHours, MinFrequencyHours, MaxFrequencyHours);
        return nowUtc - lastRunUtc.Value >= TimeSpan.FromHours(hours) - Tolerance;
    }
}
