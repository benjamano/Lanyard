using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

// Wording for operations alert notifications, shared by the email and the push. The tag is the
// alert's id, so a later notice about the same alert replaces the earlier one on the phone.
public static class OperationsAlertNotices
{
    // Admins and Managers both open the alerts page: it is where an alert is acknowledged and
    // resolved, and it links on to the page about the kiosk or rule.
    public const string AlertsPageUrl = "/manage/alerts";

    public static bool Handles(NotificationPayload payload) => payload is OperationsAlertPayload;

    public static Notice For(NotificationPayload payload)
    {
        if (payload is not OperationsAlertPayload alert)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "Not an operations alert notice.");
        }

        List<string> lines = [];

        string about = string.IsNullOrWhiteSpace(alert.SubjectName) ? string.Empty : $"{alert.SubjectName}: ";

        if (!string.IsNullOrWhiteSpace(alert.Detail))
        {
            lines.Add(about + alert.Detail);
        }
        else if (about.Length > 0)
        {
            lines.Add(about.TrimEnd(' ', ':') + ".");
        }

        lines.Add(string.IsNullOrWhiteSpace(alert.LocationName)
            ? $"{SeverityLabel(alert.Severity)} alert for the whole company."
            : $"{SeverityLabel(alert.Severity)} alert at {alert.LocationName}.");

        lines.Add("Open Operations alerts to acknowledge or resolve it.");

        return new Notice(alert.Title, lines, alert.Url, "Open alerts", $"ops-alert-{alert.AlertId:N}");
    }

    public static string SeverityLabel(OperationsAlertSeverity severity) => severity switch
    {
        OperationsAlertSeverity.Critical => "Critical",
        OperationsAlertSeverity.Warning => "Warning",
        _ => "Info"
    };
}
