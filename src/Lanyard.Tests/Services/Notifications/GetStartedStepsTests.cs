using Lanyard.Application.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Notifications;

[TestClass]
public class GetStartedStepsTests
{
    private const string IPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1";
    private const string OldIPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.3 Mobile/15E148 Safari/604.1";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";
    private const string WindowsEdge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    private static GetStartedStatus Evaluate(
        string userAgent,
        bool standalone = false,
        bool serverPush = true,
        bool pushSupported = true,
        string permission = "default",
        bool subscribed = false,
        int passkeys = 0) =>
        GetStartedSteps.Evaluate(new GetStartedInput(
            DeviceDescriptor.Describe(new DeviceReport(userAgent, 5, standalone)),
            standalone, serverPush, pushSupported, permission, subscribed, passkeys));

    [TestMethod]
    public void IPhoneInSafari_NeedsInstallBeforeNotifications()
    {
        GetStartedStatus status = Evaluate(IPhoneSafari, pushSupported: false);

        Assert.AreEqual(GetStartedInstall.Todo, status.Install);
        Assert.AreEqual(GetStartedNotifications.NeedsInstall, status.Notifications);
        Assert.IsTrue(status.NotificationsRequired);
        Assert.IsFalse(status.AllSet);
        Assert.AreEqual(0, status.StepsDone);
        Assert.AreEqual(2, status.StepsTotal);
    }

    [TestMethod]
    public void IPhoneInstalled_NotificationsOff_IsHalfway()
    {
        GetStartedStatus status = Evaluate(IPhoneSafari, standalone: true);

        Assert.AreEqual(GetStartedInstall.Done, status.Install);
        Assert.AreEqual(GetStartedNotifications.Off, status.Notifications);
        Assert.IsFalse(status.AllSet);
        Assert.AreEqual(1, status.StepsDone);
    }

    [TestMethod]
    public void AndroidInstalledWithPush_IsAllSet_WithoutAPasskey()
    {
        GetStartedStatus status = Evaluate(AndroidChrome, standalone: true, permission: "granted", subscribed: true);

        Assert.IsTrue(status.AllSet);
        Assert.IsTrue(status.CanSendTest);
        Assert.IsFalse(status.HasPasskey);
        Assert.AreEqual(2, status.StepsDone);
        Assert.AreEqual(2, status.StepsTotal);
    }

    [TestMethod]
    public void AndroidInBrowser_CanTurnOnNotificationsButStillNeedsInstall()
    {
        GetStartedStatus status = Evaluate(AndroidChrome, permission: "granted", subscribed: true);

        Assert.AreEqual(GetStartedInstall.Todo, status.Install);
        Assert.AreEqual(GetStartedNotifications.On, status.Notifications);
        Assert.IsFalse(status.AllSet);
    }

    [TestMethod]
    public void Desktop_HasNoInstallStep_AndNotificationsAreOptional()
    {
        GetStartedStatus status = Evaluate(WindowsEdge);

        Assert.AreEqual(GetStartedInstall.NotNeeded, status.Install);
        Assert.AreEqual(GetStartedNotifications.Off, status.Notifications);
        Assert.IsFalse(status.NotificationsRequired);
        Assert.IsFalse(status.AllSet, "A computer is never 'all set' - the page asks for the phone");
        Assert.AreEqual(1, status.StepsTotal);
    }

    [TestMethod]
    public void BlockedPermission_IsReported()
    {
        GetStartedStatus status = Evaluate(AndroidChrome, standalone: true, permission: "denied");

        Assert.AreEqual(GetStartedNotifications.Blocked, status.Notifications);
        Assert.IsFalse(status.AllSet);
        Assert.IsFalse(status.CanSendTest);
    }

    [TestMethod]
    public void GrantedButNoMatchingServerRow_CountsAsOff()
    {
        GetStartedStatus status = Evaluate(AndroidChrome, standalone: true, permission: "granted", subscribed: false);

        Assert.AreEqual(GetStartedNotifications.Off, status.Notifications);
    }

    [TestMethod]
    public void ServerWithoutPushKeys_DoesNotHoldAnyoneBack()
    {
        GetStartedStatus status = Evaluate(AndroidChrome, standalone: true, serverPush: false);

        Assert.AreEqual(GetStartedNotifications.ServerOff, status.Notifications);
        Assert.IsFalse(status.NotificationsRequired);
        Assert.IsTrue(status.AllSet);
    }

    [TestMethod]
    public void OldIos_CannotHavePush_SoOnlyInstallIsRequired()
    {
        GetStartedStatus status = Evaluate(OldIPhoneSafari, standalone: true, pushSupported: false);

        Assert.AreEqual(GetStartedNotifications.IosTooOld, status.Notifications);
        Assert.IsTrue(status.AllSet);
    }

    [TestMethod]
    public void Passkey_IsAccountWide_SoDoesNotCountTowardsProgress()
    {
        GetStartedStatus status = Evaluate(WindowsEdge, passkeys: 2);

        Assert.IsTrue(status.HasPasskey);
        Assert.AreEqual(0, status.StepsDone);
        Assert.AreEqual(1, status.StepsTotal);
    }
}
