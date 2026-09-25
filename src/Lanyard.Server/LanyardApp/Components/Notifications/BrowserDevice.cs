using Lanyard.Application.Services.Notifications;

namespace Lanyard.App.Components.Notifications;

// lanyardInstall.getDevice()
public record BrowserDevice(string? UserAgent, int MaxTouchPoints, bool IsStandalone, string? DeviceId)
{
    public DeviceReport Report => new(UserAgent, MaxTouchPoints, IsStandalone);
}

// lanyardPush.getState()
public record BrowserPushState(bool Supported, string Permission, string? Endpoint);

// lanyardInstall.getState()
public record BrowserInstallState(
    string? UserAgent,
    int MaxTouchPoints,
    bool IsStandalone,
    bool CanPromptNatively,
    bool PushSupported,
    string NotificationPermission,
    bool HasPushSubscription,
    int PageViewsThisSession,
    bool ShownThisSession,
    DateTime? InstallSnoozedUntilUtc,
    bool InstallNeverAsk,
    DateTime? NotifySnoozedUntilUtc,
    bool NotifyNeverAsk)
{
    public InstallPromptState ToPromptState() => new(
        DeviceDescriptor.Describe(new DeviceReport(UserAgent, MaxTouchPoints, IsStandalone)),
        IsStandalone,
        CanPromptNatively,
        PushSupported,
        NotificationPermission,
        HasPushSubscription,
        PageViewsThisSession,
        ShownThisSession,
        InstallSnoozedUntilUtc?.ToUniversalTime(),
        InstallNeverAsk,
        NotifySnoozedUntilUtc?.ToUniversalTime(),
        NotifyNeverAsk);
}
