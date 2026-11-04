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
public class KioskNotificationTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);

    private static readonly Guid ClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly KioskOfflinePayload Offline = new(3, ClientId, "Arena PC", "Ipswich", "13:53", false, true, KioskNotices.AdminUrl);

    private static readonly KioskBackOnlinePayload BackOnline = new(3, ClientId, "Arena PC", "Ipswich", 37, KioskNotices.AdminUrl);

    private static readonly KioskNeedsAttentionPayload NeedsAttention = new(3, ClientId, "Arena PC", "Ipswich", ["DMX device not open"], KioskNotices.ManagerUrl);

    [TestMethod]
    public void Topics_AreManagerOnlyInTheirOwnGroup()
    {
        NotificationTopicInfo offline = NotificationTopics.Get(NotificationTopic.KioskOffline);
        NotificationTopicInfo attention = NotificationTopics.Get(NotificationTopic.KioskNeedsAttention);

        Assert.AreEqual(NotificationTopicGroup.Kiosks, offline.Group);
        Assert.AreEqual("Kiosks", NotificationTopics.GroupLabel(NotificationTopicGroup.Kiosks));
        Assert.IsTrue(offline.ManagerOnly && attention.ManagerOnly);

        // No preference row means the default, so existing users need no data migration.
        Assert.AreEqual(new TopicPreference(NotificationTopic.KioskOffline, true, true), NotificationTopics.Default(NotificationTopic.KioskOffline));
        Assert.AreEqual(new TopicPreference(NotificationTopic.KioskNeedsAttention, true, false), NotificationTopics.Default(NotificationTopic.KioskNeedsAttention));
    }

    [TestMethod]
    public void Push_OfflineIsUrgentAndBackOnlineReplacesIt()
    {
        PushContent offline = PushContentBuilder.Build(Offline, DateOnly.FromDateTime(Now));
        PushContent back = PushContentBuilder.Build(BackOnline, DateOnly.FromDateTime(Now));
        PushContent attention = PushContentBuilder.Build(NeedsAttention, DateOnly.FromDateTime(Now));

        Assert.AreEqual("Arena PC is offline", offline.Title);
        StringAssert.Contains(offline.Body, "stopped responding at 13:53");
        StringAssert.Contains(offline.Body, "offline copy");
        Assert.AreEqual(PushMessageUrgency.High, offline.Urgency);
        Assert.AreEqual("/manage/clients", offline.Url);

        Assert.AreEqual("Arena PC is back online", back.Title);
        StringAssert.Contains(back.Body, "after 37 minutes");
        Assert.AreEqual(offline.Tag, back.Tag);

        Assert.AreEqual("Arena PC needs attention", attention.Title);
        StringAssert.Contains(attention.Body, "DMX device not open");
        Assert.AreEqual(PushMessageUrgency.Normal, attention.Urgency);
        Assert.AreEqual("/", attention.Url);
    }

    [TestMethod]
    public void Notice_SaysWhenAKioskWasShutDownRatherThanLost()
    {
        Notice notice = KioskNotices.For(Offline with { ShutDownCleanly = true, OfflineCopyReady = false, LocationName = null });

        StringAssert.StartsWith(notice.Lines[0], "Arena PC was shut down or closed at 13:53");
        StringAssert.Contains(notice.Lines[1], "may have stopped");
    }

    [TestMethod]
    public async Task Deliverer_EmailsEachKioskAlertThePersonGetsByEmail()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = "amy", UserName = "amy", FirstName = "Amy", Email = "amy@example.com" });

            // "Needs attention" is push-only by default; this person has asked for the email too.
            ctx.NotificationPreferences.Add(new NotificationPreference { Id = Guid.NewGuid(), UserId = "amy", Topic = NotificationTopic.KioskNeedsAttention, Push = true, Email = true });
            await ctx.SaveChangesAsync();
        }

        Mock<IEmailService> email = new();
        email.SetReturnsDefault(Task.FromResult(Result<bool>.Ok(true)));

        Mock<ITrainingBrandingResolver> branding = new();
        branding.Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new TrainingBranding("#C8102E", 7, null));

        NotificationDeliverer deliverer = new(
            SchedulingTestHelpers.GetFactory(options), email.Object,
            Microsoft.Extensions.Options.Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example.com/" }),
            branding.Object, Mock.Of<IPushSender>(), new TestClock(Now), NullLogger<NotificationDeliverer>.Instance);

        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.KioskOffline, Offline));
        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.KioskOffline, BackOnline));
        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.KioskNeedsAttention, NeedsAttention));

        email.Verify(x => x.SendNoticeEmailAsync(It.Is<UserProfile>(u => u.Id == "amy"), "Arena PC is offline", It.IsAny<IReadOnlyList<string>>(),
            "Open Lanyard", "https://lanyard.example.com/manage/clients", It.IsAny<string?>(), "#C8102E"), Times.Once);
        email.Verify(x => x.SendNoticeEmailAsync(It.IsAny<UserProfile>(), "Arena PC is back online", It.IsAny<IReadOnlyList<string>>(),
            "Open Lanyard", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Once);
        email.Verify(x => x.SendNoticeEmailAsync(It.IsAny<UserProfile>(), "Arena PC needs attention", It.IsAny<IReadOnlyList<string>>(),
            "Open Lanyard", It.Is<string>(url => url.TrimEnd('/') == "https://lanyard.example.com"), It.IsAny<string?>(), It.IsAny<string>()), Times.Once);
    }
}
