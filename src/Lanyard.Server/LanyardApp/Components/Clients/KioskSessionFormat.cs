namespace Lanyard.App.Components.Clients;

/// <summary>
/// How long a kiosk has been connected, as the Clients page's Session Length column shows it:
/// 01:05:09, or 2d 01:05:09 past a day. wwwroot/js/kioskSessionClock.js ticks the same format in
/// the browser, so keep the two in step.
/// </summary>
public static class KioskSessionFormat
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        string clock = $"{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        return elapsed.Days > 0 ? $"{elapsed.Days}d {clock}" : clock;
    }
}
