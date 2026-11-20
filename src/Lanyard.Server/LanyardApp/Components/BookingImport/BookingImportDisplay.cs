using Lanyard.Application.Services.BookingImport;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.App.Components.BookingImport;

// How an import run's status reads on screen. The tone keeps colour decisions out of the markup and
// independent of Fluent UI, so this can be unit-tested; the page maps a tone to an icon and colour.
public enum BookingImportTone
{
    Positive,
    Caution,
    Negative,
    Neutral
}

public static class BookingImportDisplay
{
    public static string StatusLabel(BookingImportRunStatus status) => status switch
    {
        BookingImportRunStatus.Success => "Success",
        BookingImportRunStatus.PartialSuccess => "Partial",
        BookingImportRunStatus.NoChanges => "No changes",
        BookingImportRunStatus.LoginFailed => "Login failed",
        BookingImportRunStatus.Failed => "Failed",
        BookingImportRunStatus.Skipped => "Skipped",
        _ => "Unknown"
    };

    public static BookingImportTone StatusTone(BookingImportRunStatus status) => status switch
    {
        BookingImportRunStatus.Success => BookingImportTone.Positive,
        BookingImportRunStatus.NoChanges => BookingImportTone.Positive,
        BookingImportRunStatus.PartialSuccess => BookingImportTone.Caution,
        BookingImportRunStatus.LoginFailed => BookingImportTone.Negative,
        BookingImportRunStatus.Failed => BookingImportTone.Negative,
        BookingImportRunStatus.Skipped => BookingImportTone.Neutral,
        _ => BookingImportTone.Neutral
    };

    public static string TriggerLabel(BookingImportTrigger trigger) => trigger switch
    {
        BookingImportTrigger.Scheduled => "Scheduled",
        BookingImportTrigger.Manual => "Run now",
        _ => trigger.ToString()
    };

    // "12 new, 3 updated" / "Nothing to import" / "2 couldn't be imported". A compact line for the
    // history row, driven by the counts rather than the status.
    public static string Counts(BookingImportRunView run)
    {
        List<string> parts = [];

        if (run.Imported > 0)
        {
            parts.Add($"{run.Imported} new");
        }

        if (run.Updated > 0)
        {
            parts.Add($"{run.Updated} updated");
        }

        if (run.Failed > 0)
        {
            parts.Add(run.Failed == 1 ? "1 couldn't be imported" : $"{run.Failed} couldn't be imported");
        }

        if (parts.Count == 0)
        {
            return run.BookingsSeen == 0 ? "No bookings found" : "Nothing changed";
        }

        return string.Join(", ", parts);
    }

    // "2 minutes ago" style relative time for the most recent run.
    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return "just now";
        }

        if (elapsed.TotalMinutes < 60)
        {
            int minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (elapsed.TotalHours < 24)
        {
            int hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        int days = (int)elapsed.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }
}
