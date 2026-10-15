namespace Lanyard.Shared.Contracts;

// Kiosk-facing routes for the offline fallback. Both the server controller and the kiosk build
// URLs from these, so the two can't drift apart.
public static class KioskOfflineRoutes
{
    public const string ManifestTemplate = "api/kiosk-offline/{clientId:guid}/manifest";
    public const string MediaTemplate = "api/kiosk-offline/{clientId:guid}/media/{fileId:guid}";

    public static string Manifest(Guid clientId) => $"api/kiosk-offline/{clientId}/manifest";

    public static string Media(Guid clientId, Guid fileId) => $"api/kiosk-offline/{clientId}/media/{fileId}";
}

// Defaults and allowed ranges for a kiosk's offline settings, shared by the admin form, the
// server-side validation and the kiosk.
public static class KioskOfflineLimits
{
    public const bool DefaultEnabled = true;

    public const int DefaultOfflineAfterSeconds = 30;
    public const int MinOfflineAfterSeconds = 10;
    public const int MaxOfflineAfterSeconds = 600;

    public const int DefaultCacheLimitMb = 2048;
    public const int MinCacheLimitMb = 256;
}
