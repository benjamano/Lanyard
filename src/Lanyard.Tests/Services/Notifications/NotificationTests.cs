using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Notifications;

[TestClass]
public class NotificationTests
{
    // Thursday 24 September 2026, 11:00 UTC (12:00 in Peterborough).
    private static readonly DateTime Now = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);

    private static readonly EmailOptions Options = new() { PublicBaseUrl = "https://lanyard.example.com/" };

    private static (NotificationDeliverer Deliverer, Mock<IEmailService> Email) GetDeliverer(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IEmailService> email = new();
        email.SetReturnsDefault(Task.FromResult(Result<bool>.Ok(true)));

        Mock<ITrainingBrandingResolver> branding = new();
        branding.Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new TrainingBranding("#C8102E", 7, Guid.Parse("11111111-1111-1111-1111-111111111111")));

        NotificationDeliverer deliverer = new(
            SchedulingTestHelpers.GetFactory(options), email.Object, Microsoft.Extensions.Options.Options.Create(Options),
            branding.Object, new TestClock(Now), NullLogger<NotificationDeliverer>.Instance);

        return (deliverer, email);
    }

    private static async Task<UserProfile> SeedUserAsync(DbContextOptions<ApplicationDbContext> options, string id = "amy")
    {
        await using ApplicationDbContext ctx = new(options);
        UserProfile user = new() { Id = id, UserName = id, FirstName = "Amy", Email = $"{id}@example.com" };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    [TestMethod]
    public void Dispatcher_QueuesOneJobPerPersonAndSkipsThePlaceholder()
    {
        NotificationDispatcher dispatcher = new(NullLogger<NotificationDispatcher>.Instance);
        RotaChangedPayload payload = new(3, "Peterborough", [], [], []);

        dispatcher.Enqueue(["amy", "tom", "amy", ApplicationDbContext.SystemDeletedUserPlaceholderId, ""], NotificationTopic.RotaChanged, payload);

        List<string> queued = [];

        while (dispatcher.Reader.TryRead(out NotificationJob? job))
        {
            queued.Add(job.UserId);
        }

        CollectionAssert.AreEqual(new[] { "amy", "tom" }, queued);
    }

    [TestMethod]
    public void Dispatcher_DropsTheOldestWhenFull()
    {
        NotificationDispatcher dispatcher = new(NullLogger<NotificationDispatcher>.Instance);
        RotaChangedPayload payload = new(3, "Peterborough", [], [], []);

        dispatcher.Enqueue(Enumerable.Range(0, NotificationDispatcher.Capacity + 2).Select(i => $"user-{i}"), NotificationTopic.RotaChanged, payload);

        Assert.IsTrue(dispatcher.Reader.TryRead(out NotificationJob? first));
        Assert.AreEqual("user-2", first!.UserId);
    }

    [TestMethod]
    public async Task Deliverer_SendsTheRotaEmailWithLinkLogoAndColour()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedUserAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email) = GetDeliverer(options);
        List<ShiftEmailLine> added = [new(new DateOnly(2026, 10, 5), "09:00–17:00", null)];

        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.RotaChanged, new RotaChangedPayload(3, "Peterborough", added, [], [])));

        email.Verify(x => x.SendRotaChangedEmailAsync(
            It.Is<UserProfile>(u => u.Id == "amy"), "Peterborough", added, It.IsAny<IReadOnlyList<ShiftEmailLine>>(), It.IsAny<IReadOnlyList<ShiftEmailLine>>(),
            "https://lanyard.example.com/rota",
            "https://lanyard.example.com/api/companies/7/logo?v=11111111111111111111111111111111",
            "#C8102E"), Times.Once);
    }

    [TestMethod]
    public async Task Deliverer_SaysTomorrowForATomorrowShift()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedUserAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email) = GetDeliverer(options);

        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.ShiftReminder,
            new ShiftReminderPayload(3, "Peterborough", new ShiftEmailLine(new DateOnly(2026, 9, 25), "09:00–17:00", null))));

        email.Verify(x => x.SendShiftReminderEmailAsync(It.IsAny<UserProfile>(), "Peterborough", It.IsAny<ShiftEmailLine>(), "tomorrow",
            "https://lanyard.example.com/rota", It.IsAny<string?>(), It.IsAny<string>()), Times.Once);
    }

    [TestMethod]
    public async Task Deliverer_LinksManagersToTheRequestsPageAndStaffToTheirTimeOff()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedUserAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email) = GetDeliverer(options);
        DateOnly start = new(2026, 10, 5);

        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.TimeOffRequested,
            new TimeOffRequestedPayload(3, "Tom Hughes", "Paid holiday", start, start, "1 day (8 h)", null)));
        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.TimeOffDecided,
            new TimeOffDecidedPayload(3, "Paid holiday", start, start, TimeOffEmailOutcome.Approved, null, "Sam Okafor")));

        email.Verify(x => x.SendTimeOffRequestedEmailAsync(It.IsAny<UserProfile>(), "Tom Hughes", "Paid holiday", start, start, "1 day (8 h)", null,
            "https://lanyard.example.com/manage/rota/time-off", It.IsAny<string?>(), It.IsAny<string>()), Times.Once);
        email.Verify(x => x.SendTimeOffDecisionEmailAsync(It.IsAny<UserProfile>(), "Paid holiday", start, start, TimeOffEmailOutcome.Approved, null, "Sam Okafor",
            "https://lanyard.example.com/rota/time-off", It.IsAny<string?>(), It.IsAny<string>()), Times.Once);
    }

    [TestMethod]
    public async Task Deliverer_SkipsSomeoneWhoNoLongerExists()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (NotificationDeliverer deliverer, Mock<IEmailService> email) = GetDeliverer(options);

        await deliverer.DeliverAsync(new NotificationJob("gone", NotificationTopic.RotaChanged, new RotaChangedPayload(3, "Peterborough", [], [], [])));

        email.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Deliverer_DoesNotThrowWhenTheEmailFails()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await SeedUserAsync(options);
        (NotificationDeliverer deliverer, Mock<IEmailService> email) = GetDeliverer(options);
        email.Setup(x => x.SendRotaChangedEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ShiftEmailLine>>(),
                It.IsAny<IReadOnlyList<ShiftEmailLine>>(), It.IsAny<IReadOnlyList<ShiftEmailLine>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Resend down"));

        await deliverer.DeliverAsync(new NotificationJob("amy", NotificationTopic.RotaChanged, new RotaChangedPayload(3, "Peterborough", [], [], [])));
    }
}
