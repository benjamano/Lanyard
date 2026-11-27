namespace Lanyard.Application.Services.Notifications;

public enum GetStartedInstall
{
    // Computers: nothing to install.
    NotNeeded,
    Todo,
    Done
}

public enum GetStartedNotifications
{
    // The server has no push keys, so there's nothing anyone can switch on.
    ServerOff,

    // iPhone/iPad in a browser tab: push only exists once the app is on the Home Screen.
    NeedsInstall,

    // iOS older than 16.4: no web push at all, even installed.
    IosTooOld,

    // A browser without push (some in-app browsers, old Firefox forks).
    Unsupported,
    Blocked,
    Off,
    On
}

// What the browser and server say about this device, for the Get Started page.
public record GetStartedInput(
    DeviceDescription Device,
    bool IsStandalone,
    bool ServerPushConfigured,
    bool PushSupported,
    string NotificationPermission,
    // The browser has a push subscription and the server holds a matching row for this person.
    bool SubscribedHere,
    int PasskeyCount);

public record GetStartedStatus(
    GetStartedInstall Install,
    GetStartedNotifications Notifications,
    bool NotificationsRequired,
    bool HasPasskey,
    int StepsDone,
    int StepsTotal)
{
    // The steps that matter for using Lanyard day to day: the app on the phone and notifications on.
    // A passkey is a nice extra, so it counts towards progress but not towards "all set".
    public bool AllSet =>
        Install != GetStartedInstall.Todo
        && (!NotificationsRequired || Notifications == GetStartedNotifications.On);

    public bool CanSendTest => Notifications == GetStartedNotifications.On;
}

// The decision behind the /get-started page, kept here so it's unit-tested apart from the browser.
public static class GetStartedSteps
{
    public static GetStartedStatus Evaluate(GetStartedInput input)
    {
        DeviceDescription device = input.Device;

        GetStartedInstall install = !device.IsPhoneOrTablet ? GetStartedInstall.NotNeeded
            : input.IsStandalone ? GetStartedInstall.Done
            : GetStartedInstall.Todo;

        GetStartedNotifications notifications = !input.ServerPushConfigured ? GetStartedNotifications.ServerOff
            : device.IsApple && !device.IosSupportsPush ? GetStartedNotifications.IosTooOld
            : !input.PushSupported && device.IsApple && !input.IsStandalone ? GetStartedNotifications.NeedsInstall
            : !input.PushSupported ? GetStartedNotifications.Unsupported
            : input.NotificationPermission == "denied" ? GetStartedNotifications.Blocked
            : input.SubscribedHere ? GetStartedNotifications.On
            : GetStartedNotifications.Off;

        // On a phone, notifications are the point of the exercise - unless this phone can't have
        // them at all. On a computer they're optional; people set up on their phone.
        bool notificationsRequired = device.IsPhoneOrTablet && notifications is not (
            GetStartedNotifications.ServerOff or GetStartedNotifications.IosTooOld or GetStartedNotifications.Unsupported);

        bool hasPasskey = input.PasskeyCount > 0;

        int total = 1; // passkey
        int done = hasPasskey ? 1 : 0;

        if (install != GetStartedInstall.NotNeeded)
        {
            total++;
            done += install == GetStartedInstall.Done ? 1 : 0;
        }

        if (notificationsRequired)
        {
            total++;
            done += notifications == GetStartedNotifications.On ? 1 : 0;
        }

        return new GetStartedStatus(install, notifications, notificationsRequired, hasPasskey, done, total);
    }
}
