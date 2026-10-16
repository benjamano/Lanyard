using Lanyard.Infrastructure.DTO.Notifications;

namespace Lanyard.Application.Services.Notifications;

// Wording for kiosk health alerts, shared by the email and the push. Every notice about one kiosk
// has the same tag, so "back online" replaces the "offline" push on the phone.
public static class KioskNotices
{
    // Admins can act on the Clients page; Managers can't open it, so they go to the dashboard,
    // where the Kiosk Health widget lives.
    public const string AdminUrl = "/manage/clients";
    public const string ManagerUrl = "/";

    public static bool Handles(NotificationPayload payload) =>
        payload is KioskOfflinePayload or KioskBackOnlinePayload or KioskNeedsAttentionPayload;

    public static Notice For(NotificationPayload payload) => payload switch
    {
        KioskOfflinePayload offline => new Notice(
            $"{offline.ClientName} is offline",
            OfflineLines(offline),
            offline.Url, "Open Lanyard", Tag(offline.ClientId)),

        KioskBackOnlinePayload back => new Notice(
            $"{back.ClientName} is back online",
            [$"{Where(back.ClientName, back.LocationName)} is connected again after {Duration(back.DownForMinutes)}."],
            back.Url, "Open Lanyard", Tag(back.ClientId)),

        KioskNeedsAttentionPayload attention => new Notice(
            $"{attention.ClientName} needs attention",
            [.. attention.Reasons, $"{Where(attention.ClientName, attention.LocationName)} has reported this for at least 10 minutes."],
            attention.Url, "Open Lanyard", Tag(attention.ClientId)),

        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "Not a kiosk notice.")
    };

    private static List<string> OfflineLines(KioskOfflinePayload offline)
    {
        List<string> lines =
        [
            offline.ShutDownCleanly
                ? $"{Where(offline.ClientName, offline.LocationName)} was shut down or closed at {offline.DownSince} and hasn't come back."
                : $"{Where(offline.ClientName, offline.LocationName)} stopped responding at {offline.DownSince}."
        ];

        lines.Add(offline.OfflineCopyReady
            ? "Its screens should still be playing their offline copy, but music changes and live scores won't reach it."
            : "Its screens and music may have stopped. Check that the PC is on and connected to the internet.");

        return lines;
    }

    private static string Where(string clientName, string? locationName) =>
        string.IsNullOrWhiteSpace(locationName) ? clientName : $"{clientName} at {locationName}";

    private static string Duration(int minutes) => minutes switch
    {
        < 2 => "about a minute",
        < 60 => $"{minutes} minutes",
        < 120 => "about an hour",
        < 60 * 24 => $"{minutes / 60} hours",
        _ => $"{minutes / (60 * 24)} days"
    };

    private static string Tag(Guid clientId) => $"kiosk-{clientId:N}";
}
