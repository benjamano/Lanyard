using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Feedback;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Feedback;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Feedback;

[TestClass]
public class FeedbackServiceTests
{
    private const string StaffUserId = "staff-user";
    private const string PlatformAdminUserId = "platform-admin";
    private const string CompanyAdminUserId = "company-admin";

    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static FeedbackService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IEmailService>? email = null,
        bool isPlatformAdmin = false,
        TestClock? clock = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISystemDbContextFactory> systemFactory = new();
        systemFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ITenantContext> tenant = new();
        tenant.SetupGet(x => x.IsPlatformAdmin).Returns(isPlatformAdmin);

        Mock<ISecurityService> security = new();
        security.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(PlatformAdminUserId));

        email ??= EmailThatSucceeds();

        return new FeedbackService(
            factory.Object,
            systemFactory.Object,
            tenant.Object,
            security.Object,
            email.Object,
            Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example" }),
            clock ?? new TestClock(Now),
            NullLogger<FeedbackService>.Instance);
    }

    private static Mock<IEmailService> EmailThatSucceeds()
    {
        Mock<IEmailService> email = new();
        email.Setup(x => x.SendFeedbackReceivedEmailAsync(It.IsAny<UserProfile>(), It.IsAny<FeedbackEmail>()))
            .ReturnsAsync(Result<bool>.Ok(true));
        return email;
    }

    private static async Task SeedPeopleAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Companies.Add(new Company { Id = 1, Name = "Play2Day" });
        ctx.Users.AddRange(
            new UserProfile { Id = StaffUserId, UserName = "sam", FirstName = "Sam", LastName = "Taylor", Email = "sam@example.com" },
            new UserProfile { Id = PlatformAdminUserId, UserName = "ops", Email = "ops@lanyard.example" },
            new UserProfile { Id = CompanyAdminUserId, UserName = "boss", Email = "boss@play2day.example" });

        ApplicationRole platformAdmin = new() { Id = "role-platform", Name = LanyardRoles.PlatformAdmin, NormalizedName = "PLATFORMADMIN", CreatedByUserId = "seed", IsActive = true };
        ApplicationRole admin = new() { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN", CreatedByUserId = "seed", IsActive = true };
        ctx.Roles.AddRange(platformAdmin, admin);
        ctx.UserRoles.AddRange(
            new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = PlatformAdminUserId, RoleId = platformAdmin.Id },
            new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = CompanyAdminUserId, RoleId = admin.Id });

        await ctx.SaveChangesAsync();
    }

    private static FeedbackSubmission Thumbs(int score, string? comment = null, string page = "rota?week=3") =>
        new(StaffUserId, 1, null, FeedbackKind.Thumbs, score, comment, page);

    [TestMethod]
    [DataRow(FeedbackKind.Thumbs, 1)]
    [DataRow(FeedbackKind.Thumbs, -1)]
    [DataRow(FeedbackKind.StarRating, 1)]
    [DataRow(FeedbackKind.StarRating, 5)]
    public async Task SubmitAsync_SavesValidFeedback(FeedbackKind kind, int score)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);

        Result<UserFeedback> result = await GetService(options).SubmitAsync(new(StaffUserId, 1, 7, kind, score, "  Love the rota  ", "rota"));

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        UserFeedback saved = await ctx.UserFeedback.SingleAsync();
        Assert.AreEqual(kind, saved.Kind);
        Assert.AreEqual(score, saved.Score);
        Assert.AreEqual("Love the rota", saved.Comment);
        Assert.AreEqual("/rota", saved.PagePath);
        Assert.AreEqual(1, saved.CompanyId);
        Assert.AreEqual(7, saved.LocationId);
        Assert.AreEqual(Now, saved.CreatedUtc);
    }

    [TestMethod]
    [DataRow(FeedbackKind.Thumbs, 0)]
    [DataRow(FeedbackKind.Thumbs, 2)]
    [DataRow(FeedbackKind.StarRating, 0)]
    [DataRow(FeedbackKind.StarRating, 6)]
    public async Task SubmitAsync_RefusesAnOutOfRangeScore(FeedbackKind kind, int score)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<UserFeedback> result = await GetService(options).SubmitAsync(new(StaffUserId, 1, null, kind, score, null, "/"));

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.UserFeedback.CountAsync());
    }

    [TestMethod]
    public async Task SubmitAsync_RefusesAnOverlongComment_AndBlankCommentIsStoredAsNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        FeedbackService service = GetService(options);

        Result<UserFeedback> tooLong = await service.SubmitAsync(Thumbs(1, new string('a', FeedbackService.MaxCommentLength + 1)));
        Result<UserFeedback> blank = await service.SubmitAsync(Thumbs(1, "   "));

        Assert.IsFalse(tooLong.IsSuccess);
        Assert.IsTrue(blank.IsSuccess, blank.Error);
        Assert.IsNull(blank.Data!.Comment);
    }

    [TestMethod]
    public async Task SubmitAsync_KeepsOnlyThePath_AndCapsItsLength()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<UserFeedback> result = await GetService(options).SubmitAsync(Thumbs(1, page: "/" + new string('x', 900)));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(FeedbackService.MaxPagePathLength, result.Data!.PagePath.Length);
        Assert.IsTrue(result.Data.PagePath.StartsWith('/'));
    }

    [TestMethod]
    public async Task SubmitAsync_EmailsEachPlatformAdmin_NotCompanyAdmins()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        Mock<IEmailService> email = EmailThatSucceeds();

        await GetService(options, email).SubmitAsync(Thumbs(-1, "The save button is hard to find"));

        email.Verify(x => x.SendFeedbackReceivedEmailAsync(
            It.Is<UserProfile>(u => u.Id == PlatformAdminUserId),
            It.Is<FeedbackEmail>(f =>
                f.Score == -1
                && f.Comment == "The save button is hard to find"
                && f.SenderName == "Sam Taylor"
                && f.SenderEmail == "sam@example.com"
                && f.CompanyName == "Play2Day"
                && f.PageUrl == "https://lanyard.example/rota?week=3"
                && f.InboxUrl == "https://lanyard.example/manage/feedback")),
            Times.Once);
        email.Verify(x => x.SendFeedbackReceivedEmailAsync(It.Is<UserProfile>(u => u.Id == CompanyAdminUserId), It.IsAny<FeedbackEmail>()), Times.Never);
    }

    [TestMethod]
    public async Task SubmitAsync_DoesNotEmailAPlatformAdminWhoseRoleIsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        await using (ApplicationDbContext ctx = new(options))
        {
            ApplicationRole role = await ctx.Roles.SingleAsync(x => x.Name == LanyardRoles.PlatformAdmin);
            role.IsActive = false;
            await ctx.SaveChangesAsync();
        }
        Mock<IEmailService> email = EmailThatSucceeds();

        Result<UserFeedback> result = await GetService(options, email).SubmitAsync(Thumbs(1));

        Assert.IsTrue(result.IsSuccess, result.Error);
        email.Verify(x => x.SendFeedbackReceivedEmailAsync(It.IsAny<UserProfile>(), It.IsAny<FeedbackEmail>()), Times.Never);
    }

    [TestMethod]
    public async Task SubmitAsync_StillSucceeds_WhenTheEmailFails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        Mock<IEmailService> email = new();
        email.Setup(x => x.SendFeedbackReceivedEmailAsync(It.IsAny<UserProfile>(), It.IsAny<FeedbackEmail>()))
            .ThrowsAsync(new HttpRequestException("Resend is down"));

        Result<UserFeedback> result = await GetService(options, email).SubmitAsync(Thumbs(1));

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.UserFeedback.CountAsync());
    }

    [TestMethod]
    public async Task SubmitAsync_LimitsHowOftenOnePersonCanSend()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TestClock clock = new(Now);
        FeedbackService service = GetService(options, clock: clock);

        for (int i = 0; i < FeedbackService.MaxSubmissionsPerHour; i++)
        {
            Assert.IsTrue((await service.SubmitAsync(Thumbs(1))).IsSuccess);
        }

        Assert.IsFalse((await service.SubmitAsync(Thumbs(1))).IsSuccess, "One more in the same hour is refused.");

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.IsTrue((await service.SubmitAsync(Thumbs(1))).IsSuccess, "An hour later they can send again.");
    }

    [TestMethod]
    public async Task InboxMethods_AreRefusedForAnyoneButAPlatformAdmin()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        FeedbackService service = GetService(options, isPlatformAdmin: false);

        Assert.IsFalse((await service.GetInboxAsync(FeedbackInboxFilter.All, false)).IsSuccess);
        Assert.IsFalse((await service.CountUnreviewedAsync()).IsSuccess);
        Assert.IsFalse((await service.SetReviewedAsync(Guid.NewGuid(), true)).IsSuccess);
    }

    [TestMethod]
    public async Task GetInboxAsync_FiltersAndNamesTheSender_NewestFirst()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        TestClock clock = new(Now);
        FeedbackService staff = GetService(options, clock: clock);
        await staff.SubmitAsync(Thumbs(1, "Up"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await staff.SubmitAsync(Thumbs(-1, "Down"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await staff.SubmitAsync(new(StaffUserId, 1, null, FeedbackKind.StarRating, 4, "Stars", "/"));

        FeedbackService admin = GetService(options, isPlatformAdmin: true, clock: clock);

        List<FeedbackInboxItem> all = (await admin.GetInboxAsync(FeedbackInboxFilter.All, false)).Data!;
        CollectionAssert.AreEqual(new[] { "Stars", "Down", "Up" }, all.Select(x => x.Comment).ToArray());
        Assert.AreEqual("Sam Taylor", all[0].UserName);
        Assert.AreEqual("Play2Day", all[0].CompanyName);

        Assert.AreEqual("Up", (await admin.GetInboxAsync(FeedbackInboxFilter.ThumbsUp, false)).Data!.Single().Comment);
        Assert.AreEqual("Down", (await admin.GetInboxAsync(FeedbackInboxFilter.ThumbsDown, false)).Data!.Single().Comment);
        Assert.AreEqual("Stars", (await admin.GetInboxAsync(FeedbackInboxFilter.Stars, false)).Data!.Single().Comment);
    }

    [TestMethod]
    public async Task SetReviewedAsync_MarksAndUnmarks_AndTheCountFollows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        UserFeedback feedback = (await GetService(options).SubmitAsync(Thumbs(1))).Data!;
        await GetService(options).SubmitAsync(Thumbs(-1));
        FeedbackService admin = GetService(options, isPlatformAdmin: true);

        Assert.AreEqual(2, (await admin.CountUnreviewedAsync()).Data);

        Assert.IsTrue((await admin.SetReviewedAsync(feedback.Id, true)).IsSuccess);

        Assert.AreEqual(1, (await admin.CountUnreviewedAsync()).Data);
        Assert.AreEqual(1, (await admin.GetInboxAsync(FeedbackInboxFilter.All, unreviewedOnly: true)).Data!.Count);
        await using (ApplicationDbContext ctx = new(options))
        {
            UserFeedback saved = await ctx.UserFeedback.SingleAsync(x => x.Id == feedback.Id);
            Assert.AreEqual(Now, saved.ReviewedUtc);
            Assert.AreEqual(PlatformAdminUserId, saved.ReviewedByUserId);
        }

        Assert.IsTrue((await admin.SetReviewedAsync(feedback.Id, false)).IsSuccess);
        Assert.AreEqual(2, (await admin.CountUnreviewedAsync()).Data);
    }

    [TestMethod]
    public async Task ThePrompt_IsShownOnce_ThenRestsForAWeek()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        TestClock clock = new(Now);
        FeedbackService service = GetService(options, clock: clock);

        Assert.IsTrue((await service.ShouldPromptAsync(StaffUserId)).Data);

        await service.RecordPromptAsync(StaffUserId, FeedbackPromptOutcome.Shown);
        Assert.IsFalse((await service.ShouldPromptAsync(StaffUserId)).Data, "Shown counts as asked, even if ignored.");

        clock.Advance(TimeSpan.FromDays(7));
        Assert.IsTrue((await service.ShouldPromptAsync(StaffUserId)).Data);
    }

    [TestMethod]
    public async Task AStarRating_RestsThePromptForAMonth()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        TestClock clock = new(Now);
        FeedbackService service = GetService(options, clock: clock);

        await service.SubmitAsync(new(StaffUserId, 1, null, FeedbackKind.StarRating, 5, null, "/"));

        clock.Advance(TimeSpan.FromDays(29));
        Assert.IsFalse((await service.ShouldPromptAsync(StaffUserId)).Data);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.IsTrue((await service.ShouldPromptAsync(StaffUserId)).Data);
    }

    [TestMethod]
    public async Task AThumbsFromTheHeader_DoesNotRestThePrompt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        FeedbackService service = GetService(options);

        await service.SubmitAsync(Thumbs(1));

        Assert.IsTrue((await service.ShouldPromptAsync(StaffUserId)).Data);
    }

    [TestMethod]
    public async Task DontAskAgain_IsRemembered()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        TestClock clock = new(Now);
        FeedbackService service = GetService(options, clock: clock);

        await service.RecordPromptAsync(StaffUserId, FeedbackPromptOutcome.OptOut);
        clock.Advance(TimeSpan.FromDays(400));

        Assert.IsFalse((await service.ShouldPromptAsync(StaffUserId)).Data);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsTrue((await ctx.Users.SingleAsync(x => x.Id == StaffUserId)).FeedbackPromptOptOut);
    }

    [TestMethod]
    public async Task ANewAccount_IsNotAskedInItsFirstWeek()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedPeopleAsync(options);
        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.Users.SingleAsync(x => x.Id == StaffUserId)).InvitedDate = Now.AddDays(-2);
            await ctx.SaveChangesAsync();
        }

        Assert.IsFalse((await GetService(options).ShouldPromptAsync(StaffUserId)).Data);
    }
}
