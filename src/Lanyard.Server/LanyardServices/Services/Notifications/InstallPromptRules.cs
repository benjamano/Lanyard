using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

public enum InstallPromptKind
{
    None,

    // Android browsers that fired beforeinstallprompt: our Install button opens the browser's own dialog.
    InstallNative,

    // Android browsers without that event (Firefox and friends): menu steps.
    InstallAndroidSteps,

    // iPhone and iPad: Share -> Add to Home Screen steps.
    InstallIos,

    // Installed already (or Android, where push works in the browser): offer to switch notifications on.
    EnableNotifications
}

// Everything the prompt decision needs, read from the browser (lanyardInstall.getState) and the
// person's own device storage. Snoozes are per device because installing is per device.
public record InstallPromptState(
    DeviceDescription Device,
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
    bool NotifyNeverAsk);

// When the "Add Lanyard to your Home Screen" popup (and its "turn on notifications" follow-up)
// appears. Pure, so the rules are unit-tested rather than clicked through on real phones.
public static class InstallPromptRules
{
    public static readonly TimeSpan NotNowSnooze = TimeSpan.FromDays(14);

    // Not on the first page after signing in: let people get where they were going first.
    public const int MinimumPageViews = 2;

    public static InstallPromptKind Decide(InstallPromptState state, DateTime nowUtc)
    {
        if (state.ShownThisSession || state.PageViewsThisSession < MinimumPageViews || !state.Device.IsPhoneOrTablet)
        {
            return InstallPromptKind.None;
        }

        bool installAllowed = !state.InstallNeverAsk && !IsSnoozed(state.InstallSnoozedUntilUtc, nowUtc);
        bool notifyAllowed = !state.NotifyNeverAsk && !IsSnoozed(state.NotifySnoozedUntilUtc, nowUtc)
            && state.PushSupported
            && state.NotificationPermission == "default"
            && !state.HasPushSubscription;

        if (state.IsStandalone)
        {
            return notifyAllowed ? InstallPromptKind.EnableNotifications : InstallPromptKind.None;
        }

        if (state.Device.IsApple)
        {
            // iPhone Safari can't push from a browser tab, so there is no notifications offer here:
            // installing is the only way to get them.
            return installAllowed ? InstallPromptKind.InstallIos : InstallPromptKind.None;
        }

        if (state.Device.Platform == DevicePlatform.Android)
        {
            if (installAllowed)
            {
                return state.CanPromptNatively ? InstallPromptKind.InstallNative : InstallPromptKind.InstallAndroidSteps;
            }

            // Push works in an Android browser tab too, so someone who'd rather not install still
            // gets asked about notifications.
            return notifyAllowed ? InstallPromptKind.EnableNotifications : InstallPromptKind.None;
        }

        return InstallPromptKind.None;
    }

    private static bool IsSnoozed(DateTime? untilUtc, DateTime nowUtc) => untilUtc is DateTime until && until > nowUtc;
}
