using System.Net;
using System.Security.Cryptography;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Lib.Net.Http.WebPush;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Notifications;

[TestClass]
public class PushNotificationTests
{
    // Thursday 24 September 2026, 11:00 UTC (12:00 in Peterborough).
    private static readonly DateTime Now = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);

    private const string IPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1";
    private const string OldIPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.3 Mobile/15E148 Safari/604.1";
    private const string IPhoneChrome = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1";
    private const string MacSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";
    private const string AndroidSamsung = "Mozilla/5.0 (Linux; Android 14; SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36";
    private const string AndroidFirefox = "Mozilla/5.0 (Android 14; Mobile; rv:125.0) Gecko/125.0 Firefox/125.0";
    private const string WindowsEdge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    // ---- Device descriptions -------------------------------------------------------------

    [TestMethod]
    [DataRow(IPhoneSafari, 5, false, DevicePlatform.IPhone, "Safari on iPhone")]
    [DataRow(IPhoneChrome, 5, false, DevicePlatform.IPhone, "Chrome on iPhone")]
    [DataRow(MacSafari, 5, false, DevicePlatform.IPad, "Safari on iPad")]
    [DataRow(MacSafari, 0, false, DevicePlatform.Desktop, "Safari on Mac")]
    [DataRow(AndroidChrome, 5, false, DevicePlatform.Android, "Chrome on Android")]
    [DataRow(AndroidSamsung, 5, false, DevicePlatform.Android, "Samsung Internet on Android")]
    [DataRow(AndroidFirefox, 5, false, DevicePlatform.Android, "Firefox on Android")]
    [DataRow(WindowsEdge, 0, false, DevicePlatform.Desktop, "Edge on Windows")]
    [DataRow(IPhoneSafari, 5, true, DevicePlatform.IPhone, "Lanyard app on iPhone")]
    public void Describe_NamesPlatformAndBrowser(string userAgent, int touchPoints, bool standalone, DevicePlatform platform, string label)
    {
        DeviceDescription device = DeviceDescriptor.Describe(new DeviceReport(userAgent, touchPoints, standalone));

        Assert.AreEqual(platform, device.Platform);
        Assert.AreEqual(label, device.Label);
    }

    [TestMethod]
    public void Describe_KnowsWhichIPhonesCanPush()
    {
        Assert.IsTrue(DeviceDescriptor.Describe(new DeviceReport(IPhoneSafari, 5, false)).IosSupportsPush);
        Assert.IsFalse(DeviceDescriptor.Describe(new DeviceReport(OldIPhoneSafari, 5, false)).IosSupportsPush);

        // An iPad posing as a Mac gives no iOS version; assume it's current.
        Assert.IsTrue(DeviceDescriptor.Describe(new DeviceReport(MacSafari, 5, false)).IosSupportsPush);
    }

    // ---- Install prompt rules ------------------------------------------------------------

    private static InstallPromptState PromptState(string userAgent, int touchPoints = 5, bool standalone = false) => new(
        DeviceDescriptor.Describe(new DeviceReport(userAgent, touchPoints, standalone)),
        IsStandalone: standalone,
        CanPromptNatively: false,
        PushSupported: true,
        NotificationPermission: "default",
        HasPushSubscription: false,
        PageViewsThisSession: 2,
        ShownThisSession: false,
        InstallSnoozedUntilUtc: null,
        InstallNeverAsk: false,
        NotifySnoozedUntilUtc: null,
        NotifyNeverAsk: false);

    [TestMethod]
    public void Prompt_IPhoneInSafari_ShowsInstallSteps()
    {
        Assert.AreEqual(InstallPromptKind.InstallIos, InstallPromptRules.Decide(PromptState(IPhoneSafari), Now));
        Assert.AreEqual(InstallPromptKind.InstallIos, InstallPromptRules.Decide(PromptState(MacSafari, touchPoints: 5), Now));
    }

    [TestMethod]
    public void Prompt_NotOnFirstPageOrDesktopOrTwicePerSession()
    {
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(PromptState(IPhoneSafari) with { PageViewsThisSession = 1 }, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(PromptState(WindowsEdge, touchPoints: 0), Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(PromptState(MacSafari, touchPoints: 0), Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(PromptState(IPhoneSafari) with { ShownThisSession = true }, Now));
    }

    [TestMethod]
    public void Prompt_NotNowSnoozesUntilItExpires()
    {
        InstallPromptState snoozed = PromptState(IPhoneSafari) with { InstallSnoozedUntilUtc = Now.AddDays(3) };

        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(snoozed, Now));
        Assert.AreEqual(InstallPromptKind.InstallIos, InstallPromptRules.Decide(snoozed, Now.AddDays(4)));
    }

    [TestMethod]
    public void Prompt_DontAskAgainIsForGood()
    {
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(PromptState(IPhoneSafari) with { InstallNeverAsk = true }, Now.AddYears(1)));
    }

    [TestMethod]
    public void Prompt_Android_UsesNativeInstallWhenOffered_OtherwiseSteps()
    {
        Assert.AreEqual(InstallPromptKind.InstallNative, InstallPromptRules.Decide(PromptState(AndroidChrome) with { CanPromptNatively = true }, Now));
        Assert.AreEqual(InstallPromptKind.InstallAndroidSteps, InstallPromptRules.Decide(PromptState(AndroidFirefox), Now));
    }

    [TestMethod]
    public void Prompt_Android_DeclinedInstall_StillOffersNotifications()
    {
        InstallPromptState state = PromptState(AndroidChrome) with { InstallNeverAsk = true };

        Assert.AreEqual(InstallPromptKind.EnableNotifications, InstallPromptRules.Decide(state, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(state with { NotificationPermission = "denied" }, Now));
    }

    [TestMethod]
    public void Prompt_InstalledApp_OffersNotificationsUntilOnOrDeclined()
    {
        InstallPromptState installed = PromptState(IPhoneSafari, standalone: true);

        Assert.AreEqual(InstallPromptKind.EnableNotifications, InstallPromptRules.Decide(installed, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(installed with { HasPushSubscription = true }, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(installed with { NotificationPermission = "granted", HasPushSubscription = true }, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(installed with { NotifySnoozedUntilUtc = Now.AddDays(1) }, Now));
        Assert.AreEqual(InstallPromptKind.None, InstallPromptRules.Decide(installed with { PushSupported = false }, Now));
    }

    // ---- Push wording ----------------------------------------------------------------------

    [TestMethod]
    public void PushContent_Rota_OneShiftNamesIt_ManyAreCounted()
    {
        ShiftEmailLine shift = new(new DateOnly(2026, 10, 5), "09:00–17:00", "Supervisor");

        PushContent one = PushContentBuilder.Build(new RotaChangedPayload(1, "Peterborough", [shift], [], []), new DateOnly(2026, 9, 24));
        Assert.AreEqual("New shifts at Peterborough", one.Title);
        Assert.AreEqual("Mon 5 Oct · 09:00–17:00 · Supervisor (new)", one.Body);
        Assert.AreEqual("/rota", one.Url);

        PushContent many = PushContentBuilder.Build(new RotaChangedPayload(1, "Peterborough", [shift, shift], [shift], []), new DateOnly(2026, 9, 24));
        Assert.AreEqual("Your rota at Peterborough has changed", many.Title);
        Assert.AreEqual("2 new shifts, 1 changed", many.Body);
    }

    [TestMethod]
    public void PushContent_TimeOffRequests_FromDifferentPeopleDontReplaceEachOther()
    {
        DateOnly today = new(2026, 9, 24);
        PushContent alice = PushContentBuilder.Build(new TimeOffRequestedPayload(1, "Alice", "Paid holiday", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9), "5 days", null), today);
        PushContent bob = PushContentBuilder.Build(new TimeOffRequestedPayload(1, "Bob", "Paid holiday", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9), "5 days", null), today);

        Assert.AreNotEqual(alice.Tag, bob.Tag);
        Assert.AreNotEqual(WebPushSender.TopicHeader(alice.Tag), WebPushSender.TopicHeader(bob.Tag));
    }

    [TestMethod]
    public void PushContent_Reminder_SaysTomorrowAndIsUrgent()
    {
        PushContent content = PushContentBuilder.Build(
            new ShiftReminderPayload(1, "Peterborough", new ShiftEmailLine(new DateOnly(2026, 9, 25), "09:00–17:00", null)),
            new DateOnly(2026, 9, 24));

        Assert.AreEqual("You're working tomorrow", content.Title);
        Assert.AreEqual("09:00–17:00 at Peterborough", content.Body);
        Assert.AreEqual(PushMessageUrgency.High, content.Urgency);
    }

    [TestMethod]
    public void PushContent_TimeOff_RejectionQuotesReason()
    {
        PushContent content = PushContentBuilder.Build(
            new TimeOffDecidedPayload(1, "Paid holiday", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9), TimeOffEmailOutcome.Rejected, "Half term", "Sam"),
            new DateOnly(2026, 9, 24));

        Assert.AreEqual("Your time off wasn't approved", content.Title);
        StringAssert.Contains(content.Body, "Mon 5 – Fri 9 Oct");
        StringAssert.Contains(content.Body, "\"Half term\"");
        Assert.AreEqual("/rota/time-off", content.Url);
    }

    [TestMethod]
    public void PushContent_ChatMessage_CarriesTheLineForTheThread()
    {
        Guid id = Guid.NewGuid();
        DateOnly today = new(2026, 9, 24);

        PushContent direct = PushContentBuilder.Build(new ChatMessagePayload(id, false, "Tom Hughes", "Tom Hughes", "Are you in today?"), today);
        Assert.AreEqual("Tom Hughes", direct.Title);
        Assert.AreEqual(new ChatPushLine("Tom Hughes", "Are you in today?", null), direct.Chat);
        Assert.AreEqual($"chat-{id:N}", direct.Tag);

        PushContent group = PushContentBuilder.Build(new ChatMessagePayload(id, true, "Weekend crew", "Tom Hughes", "Running late"), today);
        Assert.AreEqual("Tom Hughes in Weekend crew", group.Title);
        Assert.AreEqual(new ChatPushLine("Tom Hughes", "Running late", "Weekend crew"), group.Chat);
    }

    [TestMethod]
    public void PushContent_ChatMessage_WithPreviewsOff_HasNoText()
    {
        PushContent content = PushContentBuilder.Build(new ChatMessagePayload(Guid.NewGuid(), false, "Tom Hughes", "Tom Hughes", ""), new DateOnly(2026, 9, 24));

        Assert.AreEqual("New message", content.Body);
        Assert.AreEqual("", content.Chat!.Text);
    }

    [TestMethod]
    public void PushContent_NonChat_HasNoChatLine()
    {
        PushContent content = PushContentBuilder.Build(
            new ShiftReminderPayload(1, "Peterborough", new ShiftEmailLine(new DateOnly(2026, 9, 25), "09:00–17:00", null)),
            new DateOnly(2026, 9, 24));

        Assert.IsNull(content.Chat);
    }

    [TestMethod]
    public void TopicHeader_KeepsOnlyAllowedCharacters()
    {
        Assert.AreEqual("shift-reminder-20260925", WebPushSender.TopicHeader("shift-reminder-20260925"));
        Assert.AreEqual(32, WebPushSender.TopicHeader("a b.c")!.Length);
        Assert.AreEqual(32, WebPushSender.TopicHeader(new string('x', 50))!.Length);

        // Long tags are hashed, not cut, so two that share a prefix stay different.
        Assert.AreNotEqual(
            WebPushSender.TopicHeader("time-off-request-12-20261005-20261009-Alice"),
            WebPushSender.TopicHeader("time-off-request-12-20261005-20261009-Bob"));
    }

    // ---- VAPID keys ------------------------------------------------------------------------

    [TestMethod]
    public void Vapid_GeneratedKeysHaveBrowserShape()
    {
        (string publicKey, string privateKey) = VapidKeys.Generate();

        Assert.AreEqual(65, FromBase64Url(publicKey).Length);
        Assert.AreEqual(0x04, FromBase64Url(publicKey)[0]);
        Assert.AreEqual(32, FromBase64Url(privateKey).Length);
    }

    [TestMethod]
    public void Vapid_ProductionWithoutKeys_IsOff_DevelopmentMakesSome()
    {
        Assert.IsFalse(VapidKeys.Create(new PushOptions(), "https://lanyard.example.com", isDevelopment: false, NullLogger.Instance).IsConfigured);
        Assert.IsTrue(VapidKeys.Create(new PushOptions(), null, isDevelopment: true, NullLogger.Instance).IsConfigured);

        (string publicKey, string privateKey) = VapidKeys.Generate();
        VapidKeys configured = VapidKeys.Create(new PushOptions { PublicKey = publicKey, PrivateKey = $" {privateKey}\n" }, "https://lanyard.example.com/", false, NullLogger.Instance);
        Assert.AreEqual(publicKey, configured.PublicKey);
        Assert.AreEqual(privateKey, configured.PrivateKey);
        Assert.AreEqual("https://lanyard.example.com", configured.Subject);
    }

    [TestMethod]
    public void Vapid_PrivateKeyFromAnotherPair_TurnsPushOff()
    {
        (string publicKey, _) = VapidKeys.Generate();
        (_, string otherPrivateKey) = VapidKeys.Generate();

        Assert.IsFalse(VapidKeys.IsMatchingPair(publicKey, otherPrivateKey));
        Assert.IsFalse(VapidKeys.Create(new PushOptions { PublicKey = publicKey, PrivateKey = otherPrivateKey }, null, false, NullLogger.Instance).IsConfigured);
        Assert.IsFalse(VapidKeys.IsMatchingPair("pub", "priv"));
    }

    // ---- Preferences -----------------------------------------------------------------------

    [TestMethod]
    public async Task Preferences_DefaultToPushAndEmail_AndOnlyStoreDifferences()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        NotificationPreferenceService service = new(SchedulingTestHelpers.GetFactory(options), NullLogger<NotificationPreferenceService>.Instance);

        Result<List<TopicPreference>> defaults = await service.GetForUserAsync("amy");
        Assert.IsTrue(defaults.Data!.All(x => x == NotificationTopics.Default(x.Topic)));
        // Push is on by default wherever a topic has it - except everyday channel chatter.
        Assert.IsTrue(defaults.Data!.Where(x => NotificationTopics.Get(x.Topic).PushAvailable && x.Topic != NotificationTopic.ChannelMessage).All(x => x.Push));
        Assert.IsFalse(defaults.Data!.Single(x => x.Topic == NotificationTopic.ChannelMessage).Push);
        Assert.IsTrue(defaults.Data!.Single(x => x.Topic == NotificationTopic.ShiftReminder).Email, "Topics that emailed before push still do");
        Assert.AreEqual(NotificationTopics.All.Count, defaults.Data!.Count);

        await service.SaveAsync("amy", NotificationTopic.ShiftReminder, push: true, email: false);

        Result<List<TopicPreference>> changed = await service.GetForUserAsync("amy");
        Assert.IsFalse(changed.Data!.Single(x => x.Topic == NotificationTopic.ShiftReminder).Email);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(1, await ctx.NotificationPreferences.CountAsync());
        }

        // Back to the default removes the row.
        await service.SaveAsync("amy", NotificationTopic.ShiftReminder, push: true, email: true);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(0, await ctx.NotificationPreferences.CountAsync());
        }
    }

    // ---- Subscriptions ---------------------------------------------------------------------

    private static PushSubscriptionService SubscriptionService(DbContextOptions<ApplicationDbContext> options, TestClock? clock = null) =>
        new(SchedulingTestHelpers.GetFactory(options), clock ?? new TestClock(Now), NullLogger<PushSubscriptionService>.Instance);

    private static PushSubscriptionInput Input(string endpoint = "https://fcm.googleapis.com/fcm/send/abc") => new(endpoint, "key", "auth");

    [TestMethod]
    public async Task Subscription_SameEndpoint_MovesToWhoeverSignedIn()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        PushSubscriptionService service = SubscriptionService(options);

        await service.SaveAsync("amy", Input(), new DeviceReport(AndroidChrome, 5, false));
        await service.SaveAsync("sam", Input(), new DeviceReport(AndroidChrome, 5, false));

        await using ApplicationDbContext ctx = new(options);
        UserPushSubscription row = await ctx.PushSubscriptions.SingleAsync();
        Assert.AreEqual("sam", row.UserId);
        Assert.AreEqual("Chrome on Android", row.DeviceLabel);
    }

    [TestMethod]
    public async Task Subscription_UnchangedResync_TouchesLastSeenAtMostHourly()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        TestClock clock = new(Now);
        PushSubscriptionService service = SubscriptionService(options, clock);
        DeviceReport device = new(AndroidChrome, 5, false);

        await service.SaveAsync("amy", Input(), device);

        clock.UtcNow = Now.AddMinutes(20);
        await service.SaveAsync("amy", Input(), device);
        Assert.AreEqual(Now, (await service.GetForUserAsync("amy")).Data!.Single().LastSeenUtc);

        clock.UtcNow = Now.AddHours(2);
        await service.SaveAsync("amy", Input(), device);
        Assert.AreEqual(Now.AddHours(2), (await service.GetForUserAsync("amy")).Data!.Single().LastSeenUtc);
    }

    [TestMethod]
    public async Task Subscription_RejectsAnythingButAnHttpsPushAddress()
    {
        PushSubscriptionService service = SubscriptionService(SchedulingTestHelpers.GetInMemoryOptions());

        Assert.IsFalse((await service.SaveAsync("amy", Input("http://example.com/push"), new DeviceReport(null, 0, false))).IsSuccess);
        Assert.IsFalse((await service.SaveAsync("amy", Input("not a url"), new DeviceReport(null, 0, false))).IsSuccess);
        Assert.IsFalse((await service.SaveAsync("amy", new PushSubscriptionInput("https://push.example.com/a", "", "auth"), new DeviceReport(null, 0, false))).IsSuccess);
    }

    [TestMethod]
    public async Task Subscription_RemoveOnlyTouchesYourOwn()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        PushSubscriptionService service = SubscriptionService(options);

        await service.SaveAsync("amy", Input(), new DeviceReport(AndroidChrome, 5, false));

        Assert.IsFalse((await service.RemoveAsync("sam", Input().Endpoint)).Data);

        Guid id = (await service.GetForUserAsync("amy")).Data!.Single().Id;
        Assert.IsFalse((await service.RemoveByIdAsync("sam", id)).IsSuccess);

        Assert.IsTrue((await service.RemoveAsync("amy", Input().Endpoint)).Data);
        Assert.AreEqual(0, (await service.GetForUserAsync("amy")).Data!.Count);
    }

    [TestMethod]
    public async Task Subscription_UnseenFor90Days_IsCleanedUp()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        TestClock clock = new(Now.AddDays(-100));
        PushSubscriptionService service = SubscriptionService(options, clock);

        await service.SaveAsync("amy", Input("https://push.example.com/old"), new DeviceReport(AndroidChrome, 5, false));
        clock.UtcNow = Now;
        await service.SaveAsync("amy", Input("https://push.example.com/new"), new DeviceReport(AndroidChrome, 5, false));

        Result<int> removed = await service.RemoveStaleAsync(Now - PushDeviceCleanupHostedService.UnseenLimit);

        Assert.AreEqual(1, removed.Data);
        Assert.AreEqual("https://push.example.com/new", (await service.GetForUserAsync("amy")).Data!.Single().Endpoint);
    }

    // ---- App installs ----------------------------------------------------------------------

    [TestMethod]
    public async Task AppInstallation_OneRowPerDevice_AndSummaryCountsBoth()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        AppInstallationService installs = new(SchedulingTestHelpers.GetFactory(options), new TestClock(Now), NullLogger<AppInstallationService>.Instance);

        await installs.RecordAsync("amy", "device-1", new DeviceReport(IPhoneSafari, 5, true));
        await installs.RecordAsync("amy", "device-1", new DeviceReport(IPhoneSafari, 5, true));
        await SubscriptionService(options).SaveAsync("amy", Input(), new DeviceReport(IPhoneSafari, 5, true));

        DeviceReachSummary summary = (await installs.GetSummaryAsync("amy")).Data!;

        Assert.AreEqual(1, summary.InstalledDevices);
        Assert.AreEqual(1, summary.PushDevices);

        await using ApplicationDbContext ctx = new(options);
        AppInstallation row = await ctx.AppInstallations.SingleAsync();
        Assert.AreEqual(DevicePlatform.IPhone, row.Platform);
        Assert.AreEqual("Lanyard app on iPhone", row.DeviceLabel);
    }

    // ---- Delivery follows preferences ------------------------------------------------------

    private static (NotificationDeliverer Deliverer, Mock<IEmailService> Email, Mock<IPushSender> Push) Deliverer(DbContextOptions<ApplicationDbContext> options, bool pushConfigured = true)
    {
        Mock<IEmailService> email = new();
        email.SetReturnsDefault(Task.FromResult(Result<bool>.Ok(true)));

        Mock<ITrainingBrandingResolver> branding = new();
        branding.Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new TrainingBranding("#C8102E", 7, null));

        Mock<IPushSender> push = new();
        push.SetupGet(x => x.IsConfigured).Returns(pushConfigured);
        push.Setup(x => x.SendToUserAsync(It.IsAny<string>(), It.IsAny<PushContent>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushSendSummary(1, 1, 0));

        NotificationDeliverer deliverer = new(
            SchedulingTestHelpers.GetFactory(options), email.Object, Microsoft.Extensions.Options.Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example.com/" }),
            branding.Object, push.Object, new TestClock(Now), NullLogger<NotificationDeliverer>.Instance);

        return (deliverer, email, push);
    }

    private static NotificationJob ReminderJob() => new("amy", NotificationTopic.ShiftReminder,
        new ShiftReminderPayload(1, "Peterborough", new ShiftEmailLine(new DateOnly(2026, 9, 25), "09:00–17:00", null)));

    private static async Task SeedAmyAsync(DbContextOptions<ApplicationDbContext> options, NotificationPreference? preference = null)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.Users.Add(new UserProfile { Id = "amy", UserName = "amy", FirstName = "Amy", Email = "amy@example.com" });

        if (preference is not null)
        {
            ctx.NotificationPreferences.Add(preference);
        }

        await ctx.SaveChangesAsync();
    }

    private static void VerifyEmailed(Mock<IEmailService> email, Times times) =>
        email.Verify(x => x.SendShiftReminderEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<ShiftEmailLine>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()), times);

    private static void VerifyPushed(Mock<IPushSender> push, Times times) =>
        push.Verify(x => x.SendToUserAsync("amy", It.IsAny<PushContent>(), null, It.IsAny<CancellationToken>()), times);

    [TestMethod]
    public async Task Deliver_Defaults_SendBothEmailAndPush()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedAmyAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email, Mock<IPushSender> push) = Deliverer(options);

        await deliverer.DeliverAsync(ReminderJob());

        VerifyEmailed(email, Times.Once());
        push.Verify(x => x.SendToUserAsync("amy", It.Is<PushContent>(c => c.Title == "You're working tomorrow"), null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [TestMethod]
    public async Task Deliver_EmailOff_OnlyPushes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedAmyAsync(options, new NotificationPreference { UserId = "amy", Topic = NotificationTopic.ShiftReminder, Push = true, Email = false });
        (NotificationDeliverer deliverer, Mock<IEmailService> email, Mock<IPushSender> push) = Deliverer(options);

        await deliverer.DeliverAsync(ReminderJob());

        VerifyEmailed(email, Times.Never());
        VerifyPushed(push, Times.Once());
    }

    [TestMethod]
    public async Task Deliver_PushOff_OnlyEmails()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedAmyAsync(options, new NotificationPreference { UserId = "amy", Topic = NotificationTopic.ShiftReminder, Push = false, Email = true });
        (NotificationDeliverer deliverer, Mock<IEmailService> email, Mock<IPushSender> push) = Deliverer(options);

        await deliverer.DeliverAsync(ReminderJob());

        VerifyEmailed(email, Times.Once());
        VerifyPushed(push, Times.Never());
    }

    [TestMethod]
    public async Task Deliver_ServerWithoutPushKeys_StillEmails()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedAmyAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email, Mock<IPushSender> push) = Deliverer(options, pushConfigured: false);

        await deliverer.DeliverAsync(ReminderJob());

        VerifyEmailed(email, Times.Once());
        VerifyPushed(push, Times.Never());
    }

    [TestMethod]
    public async Task Deliver_EmailThrowing_DoesNotStopPush()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedAmyAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email, Mock<IPushSender> push) = Deliverer(options);
        email.Setup(x => x.SendShiftReminderEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<ShiftEmailLine>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>())).ThrowsAsync(new HttpRequestException("Resend down"));

        await deliverer.DeliverAsync(ReminderJob());

        VerifyPushed(push, Times.Once());
    }

    // ---- Sending to push services ----------------------------------------------------------

    private sealed class FakePushService : HttpMessageHandler
    {
        // Status codes to answer per endpoint, in order; 201 once they run out.
        public Dictionary<string, Queue<HttpStatusCode>> Answers { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        // Read on arrival: the client disposes the encrypted body once the request is done.
        public List<long?> BodyLengths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            BodyLengths.Add(request.Content?.Headers.ContentLength);

            HttpStatusCode status = Answers.TryGetValue(request.RequestUri!.ToString(), out Queue<HttpStatusCode>? queue) && queue.Count > 0
                ? queue.Dequeue()
                : HttpStatusCode.Created;

            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }

    private static (WebPushSender Sender, FakePushService Service) Sender(DbContextOptions<ApplicationDbContext> options)
    {
        FakePushService service = new();
        Mock<IHttpClientFactory> clients = new();
        clients.Setup(x => x.CreateClient(WebPushSender.HttpClientName)).Returns(() => new HttpClient(service, disposeHandler: false));

        (string publicKey, string privateKey) = VapidKeys.Generate();
        VapidKeys keys = VapidKeys.Create(new PushOptions { PublicKey = publicKey, PrivateKey = privateKey, Subject = "mailto:test@example.com" }, null, false, NullLogger.Instance);

        WebPushSender sender = new(SchedulingTestHelpers.GetFactory(options), clients.Object, keys, new TestClock(Now), NullLogger<WebPushSender>.Instance)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
        };

        return (sender, service);
    }

    // A real browser-shaped key pair, so the payload encryption runs for real.
    private static async Task SeedDeviceAsync(DbContextOptions<ApplicationDbContext> options, string endpoint, int failures = 0)
    {
        using ECDiffieHellman client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = client.ExportParameters(false);
        byte[] publicKey = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];

        await using ApplicationDbContext ctx = new(options);
        ctx.PushSubscriptions.Add(new UserPushSubscription
        {
            UserId = "amy",
            Endpoint = endpoint,
            P256dh = ToBase64Url(publicKey),
            Auth = ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            DeviceLabel = "Chrome on Android",
            CreatedUtc = Now.AddDays(-1),
            LastSeenUtc = Now.AddDays(-1),
            ConsecutiveFailures = failures
        });
        await ctx.SaveChangesAsync();
    }

    private static PushContent Content => new("You're working tomorrow", "09:00–17:00 at Peterborough", "/rota", "shift-reminder-20260925", PushMessageUrgency.High, TimeSpan.FromHours(12));

    [TestMethod]
    public async Task Send_Delivered_SignsWithVapidAndSetsHeaders()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a", failures: 2);
        (WebPushSender sender, FakePushService service) = Sender(options);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content);

        Assert.AreEqual(1, summary.Delivered);

        HttpRequestMessage request = service.Requests.Single();
        Assert.AreEqual("vapid", request.Headers.Authorization?.Scheme);
        Assert.AreEqual("43200", request.Headers.GetValues("TTL").Single());
        Assert.AreEqual("high", request.Headers.GetValues("Urgency").Single());
        Assert.AreEqual("shift-reminder-20260925", request.Headers.GetValues("Topic").Single());
        Assert.AreEqual("aes128gcm", request.Content!.Headers.ContentEncoding.Single());

        await using ApplicationDbContext ctx = new(options);
        UserPushSubscription row = await ctx.PushSubscriptions.SingleAsync();
        Assert.AreEqual(Now, row.LastSuccessUtc);
        Assert.AreEqual(0, row.ConsecutiveFailures);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Gone)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task Send_Gone_RemovesTheDevice(HttpStatusCode status)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a");
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/a"] = new([status]);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content);

        Assert.AreEqual(1, summary.Removed);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.PushSubscriptions.CountAsync());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.BadRequest)]
    public async Task Send_RequestRefused_KeepsTheDevice(HttpStatusCode status)
    {
        // Usually our own VAPID setup is wrong; deleting would wipe every device at once.
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a");
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/a"] = new([status]);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content);

        Assert.AreEqual(0, summary.Removed);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, (await ctx.PushSubscriptions.SingleAsync()).ConsecutiveFailures);
    }

    [TestMethod]
    public async Task Send_ServerError_IsRetried()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a");
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/a"] = new([HttpStatusCode.ServiceUnavailable, HttpStatusCode.InternalServerError]);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content);

        Assert.AreEqual(1, summary.Delivered);
        Assert.AreEqual(3, service.Requests.Count);
    }

    [TestMethod]
    public async Task Send_KeepsFailing_CountsFailures_AndRemovesAfterFive()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a", failures: 3);
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/a"] = new(Enumerable.Repeat(HttpStatusCode.InternalServerError, 6));

        await sender.SendToUserAsync("amy", Content);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(4, (await ctx.PushSubscriptions.SingleAsync()).ConsecutiveFailures);
        }

        await sender.SendToUserAsync("amy", Content);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(0, await ctx.PushSubscriptions.CountAsync());
        }
    }

    [TestMethod]
    public async Task Send_TooLarge_RetriesOnceWithShorterBody()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/a");
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/a"] = new([HttpStatusCode.RequestEntityTooLarge]);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content with { Body = new string('x', 2000) });

        Assert.AreEqual(1, summary.Delivered);
        Assert.AreEqual(2, service.Requests.Count);
        Assert.IsTrue(service.BodyLengths[1] < service.BodyLengths[0]);
    }

    [TestMethod]
    public async Task Send_OneDeadDevice_DoesNotStopTheOthers()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/dead");
        await SeedDeviceAsync(options, "https://push.example.com/alive");
        (WebPushSender sender, FakePushService service) = Sender(options);
        service.Answers["https://push.example.com/dead"] = new([HttpStatusCode.Gone]);

        PushSendSummary summary = await sender.SendToUserAsync("amy", Content);

        Assert.AreEqual(2, summary.Devices);
        Assert.AreEqual(1, summary.Delivered);
        Assert.AreEqual(1, summary.Removed);
    }

    [TestMethod]
    public async Task Send_OnlyEndpoint_TargetsThatDevice()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedDeviceAsync(options, "https://push.example.com/phone");
        await SeedDeviceAsync(options, "https://push.example.com/laptop");
        (WebPushSender sender, FakePushService service) = Sender(options);

        await sender.SendToUserAsync("amy", Content, onlyEndpoint: "https://push.example.com/laptop");

        Assert.AreEqual("https://push.example.com/laptop", service.Requests.Single().RequestUri!.ToString());
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
