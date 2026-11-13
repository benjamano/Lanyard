using Humanizer;
using Lanyard.Infrastructure.DTO.OperationsAlerts;
using Lanyard.Infrastructure.Enum;
using System.Globalization;

namespace Lanyard.App.Components.OperationsAlerts;

// Shared wording for alert rows on the page and the widget.
public static class OperationsAlertFormat
{
    private static readonly CultureInfo Uk = new("en-GB");

    public static string TypeLabel(OperationsAlertType type) => type switch
    {
        OperationsAlertType.KioskOffline => "Kiosk offline",
        OperationsAlertType.KioskNeedsAttention => "Kiosk needs attention",
        OperationsAlertType.AutomationRuleFailed => "Automation rule failed",
        OperationsAlertType.MissedClockIn => "Missed clock-in",
        OperationsAlertType.TrainingOverdue => "Training overdue",
        OperationsAlertType.StaffDocumentExpiring => "Document expiring",
        OperationsAlertType.StaffDocumentExpired => "Document expired",
        _ => type.ToString()
    };

    // "12 minutes ago", from the server's clock rather than the browser's.
    public static string Ago(DateTime utc, DateTime nowUtc) =>
        nowUtc - utc < TimeSpan.FromMinutes(1) ? "just now" : (nowUtc - utc).Humanize(1, Uk) + " ago";

    // "14:02", or "Thu 16 Oct 14:02" when it wasn't today, in venue time.
    public static string At(DateTime utc, DateTime nowUtc)
    {
        DateTime local = RotaTime.ToVenueLocal(utc);

        return local.Date == RotaTime.ToVenueLocal(nowUtc).Date
            ? local.ToString("HH:mm", Uk)
            : local.ToString("ddd d MMM HH:mm", Uk);
    }

    public static string Raised(OperationsAlertView alert, DateTime nowUtc)
    {
        string raised = $"Raised {Ago(alert.RaisedAtUtc, nowUtc)}";

        // Only a failure that keeps happening says something by its count. Training, documents and
        // missed clock-ins are re-checked by a sweep, so their count is just how often it looked.
        bool countMeansSomething = alert.Type is OperationsAlertType.AutomationRuleFailed or OperationsAlertType.KioskNeedsAttention;

        return countMeansSomething && alert.OccurrenceCount > 1
            ? $"{raised} · seen {alert.OccurrenceCount} times, last {Ago(alert.LastSeenAtUtc, nowUtc)}"
            : raised;
    }

    public static string? Handled(OperationsAlertView alert, DateTime nowUtc) => alert.Status switch
    {
        OperationsAlertStatus.Acknowledged =>
            $"Acknowledged by {alert.AcknowledgedByName ?? "a former member of staff"} at {At(alert.AcknowledgedAtUtc ?? alert.RaisedAtUtc, nowUtc)}",
        OperationsAlertStatus.Resolved when alert.Resolution == OperationsAlertResolution.Auto =>
            $"Cleared automatically at {At(alert.ResolvedAtUtc ?? alert.LastSeenAtUtc, nowUtc)}",
        OperationsAlertStatus.Resolved =>
            $"Resolved by {alert.ResolvedByName ?? "a former member of staff"} at {At(alert.ResolvedAtUtc ?? alert.LastSeenAtUtc, nowUtc)}",
        _ => null
    };
}
