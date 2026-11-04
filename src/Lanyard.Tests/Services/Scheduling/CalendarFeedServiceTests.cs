using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class CalendarFeedServiceTests
{
    // Week of Monday 5 October 2026 (BST: 09:00 local is 08:00 UTC). The clock sits just before it,
    // so the week is inside the feed's window.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Manager = "manager-user";

    private static CalendarFeedService GetService(DbContextOptions<ApplicationDbContext> options, TimeProvider? clock = null) =>
        new(SchedulingTestHelpers.GetFactory(options), clock ?? new TestClock(Now), NullLogger<CalendarFeedService>.Instance);

    private static RotaService GetRotaService(DbContextOptions<ApplicationDbContext> options)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new RotaService(factory, new ContractRequirementService(factory), new RecordingNotificationDispatcher(), NullLogger<RotaService>.Instance);
    }

    private static async Task<Shift> SeedShiftAsync(
        DbContextOptions<ApplicationDbContext> options, Location location, UserProfile user, DateOnly day, int startHour, int endHour,
        bool published = true, bool isActive = true, bool removalPending = false, Guid? positionId = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = user.Id,
            StaffPositionId = positionId,
            StartUtc = RotaTime.ToUtc(day, new TimeOnly(startHour, 0)),
            EndUtc = RotaTime.ToUtc(day, new TimeOnly(endHour, 0)),
            IsActive = isActive,
            RemovalPending = removalPending,
            CreateDate = Now.AddDays(-2),
            CreateByUserId = Manager
        };

        if (published)
        {
            shift.MarkPublished(Now.AddDays(-1), Manager);
        }

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task<string> CreateTokenAsync(CalendarFeedService service, UserProfile user, Company company)
    {
        Result<CreatedCalendarFeed> created = await service.CreateAsync(user.Id, company.Id);
        Assert.IsTrue(created.IsSuccess, created.Error);
        return created.Data!.RawToken;
    }

    private static async Task<string> FeedBodyAsync(CalendarFeedService service, string token)
    {
        Result<CalendarFeedDocument> feed = await service.BuildFeedAsync(token, "https://lanyard.example");
        Assert.IsTrue(feed.IsSuccess, feed.Error);
        return feed.Data!.Body.Replace("\r\n ", string.Empty);
    }

    private static int EventCount(string body) => body.Split("BEGIN:VEVENT").Length - 1;

    [TestMethod]
    public async Task CreateAsync_StoresOnlyAHashOfTheToken_AndTheFeedServesTheShifts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");
        Shift shift = await SeedShiftAsync(options, location, ben, Monday, 9, 17, positionId: supervisor.Id);
        CalendarFeedService service = GetService(options);

        string token = await CreateTokenAsync(service, ben, company);

        Assert.IsTrue(token.Length >= 40);

        await using (ApplicationDbContext ctx = new(options))
        {
            CalendarFeedToken stored = await ctx.CalendarFeedTokens.SingleAsync();
            Assert.AreEqual(CalendarFeedService.HashToken(token), stored.TokenHash);
            Assert.IsFalse(stored.TokenHash.Contains(token));
            Assert.AreEqual(token[^4..], stored.TokenHint);
        }

        string body = await FeedBodyAsync(service, token);

        Assert.AreEqual(1, EventCount(body));
        StringAssert.Contains(body, $"UID:{shift.Id:N}@lanyard-shift");
        StringAssert.Contains(body, "DTSTART:20261005T080000Z");
        StringAssert.Contains(body, "SUMMARY:Supervisor shift");
        StringAssert.Contains(body, "LOCATION:Ipswich");
        StringAssert.Contains(body, "https://lanyard.example/rota");
        StringAssert.Contains(body, "X-WR-CALNAME:Shifts – Play2Day");
    }

    [TestMethod]
    public async Task BuildFeedAsync_SameRotaGivesTheSameETag()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        Result<CalendarFeedDocument> first = await service.BuildFeedAsync(token, null);
        Result<CalendarFeedDocument> second = await service.BuildFeedAsync(token, null);

        Assert.AreEqual(first.Data!.ETag, second.Data!.ETag);

        await SeedShiftAsync(options, location, ben, Monday.AddDays(1), 9, 17);

        Assert.AreNotEqual(first.Data.ETag, (await service.BuildFeedAsync(token, null)).Data!.ETag);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-real-token")]
    public async Task BuildFeedAsync_UnknownTokenIsNotFound(string token)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        Result<CalendarFeedDocument> result = await GetService(options).BuildFeedAsync(token, null);

        Assert.AreEqual(CalendarFeedService.NotFoundError, result.Error);
    }

    [TestMethod]
    public async Task CreateAsync_Again_RevokesTheOldLink()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        CalendarFeedService service = GetService(options);

        string oldToken = await CreateTokenAsync(service, ben, company);
        string newToken = await CreateTokenAsync(service, ben, company);

        Assert.AreNotEqual(oldToken, newToken);
        Assert.AreEqual(CalendarFeedService.NotFoundError, (await service.BuildFeedAsync(oldToken, null)).Error);
        Assert.IsTrue((await service.BuildFeedAsync(newToken, null)).IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.CalendarFeedTokens.CountAsync(x => x.IsActive));
        CalendarFeedToken revoked = await ctx.CalendarFeedTokens.SingleAsync(x => !x.IsActive);
        Assert.AreEqual(Now, revoked.RevokedDateUtc);
        Assert.AreEqual(ben.Id, revoked.RevokedByUserId);
    }

    [TestMethod]
    public async Task RevokeAsync_TurnsTheLinkOff()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        Result<bool> revoked = await service.RevokeAsync(ben.Id, company.Id);

        Assert.IsTrue(revoked.IsSuccess, revoked.Error);
        Assert.AreEqual(CalendarFeedService.NotFoundError, (await service.BuildFeedAsync(token, null)).Error);
        Assert.IsFalse((await service.GetStatusAsync(ben.Id, company.Id)).Data!.HasFeed);
        Assert.IsFalse((await service.RevokeAsync(ben.Id, company.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task GetStatusAsync_ReportsTheHintButNeverTheToken()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        CalendarFeedService service = GetService(options);

        Assert.IsFalse((await service.GetStatusAsync(ben.Id, company.Id)).Data!.HasFeed);

        string token = await CreateTokenAsync(service, ben, company);
        CalendarFeedStatus status = (await service.GetStatusAsync(ben.Id, company.Id)).Data!;

        Assert.IsTrue(status.HasFeed);
        Assert.AreEqual(token[^4..], status.TokenHint);
        Assert.AreEqual(Now, status.CreateDate);
        Assert.IsNull(status.LastAccessUtc);
    }

    [TestMethod]
    public async Task CreateAsync_FailsWithoutShiftManagementOrMembership()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        CalendarFeedService service = GetService(options);

        Assert.IsFalse((await service.CreateAsync(ben.Id, other.Id)).IsSuccess, "Not a member of the other company.");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = company.Id, Feature = CompanyFeature.StaffScheduling, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(ClockInTerminalService.FeatureOffError, (await service.CreateAsync(ben.Id, company.Id)).Error);
    }

    [TestMethod]
    public async Task BuildFeedAsync_LeavesOutDraftsAndPublishedRemovals_ButKeepsUnannouncedRemovals()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");

        Shift published = await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        Shift draft = await SeedShiftAsync(options, location, ben, Monday.AddDays(1), 9, 17, published: false);
        Shift removedAndTold = await SeedShiftAsync(options, location, ben, Monday.AddDays(2), 9, 17, isActive: false);
        Shift removedNotYetTold = await SeedShiftAsync(options, location, ben, Monday.AddDays(3), 9, 17, isActive: false, removalPending: true);
        Shift someoneElses = await SeedShiftAsync(options, location, ann, Monday.AddDays(4), 9, 17);

        CalendarFeedService service = GetService(options);
        string body = await FeedBodyAsync(service, await CreateTokenAsync(service, ben, company));

        Assert.AreEqual(2, EventCount(body));
        StringAssert.Contains(body, published.Id.ToString("N"));
        StringAssert.Contains(body, removedNotYetTold.Id.ToString("N"));
        Assert.IsFalse(body.Contains(draft.Id.ToString("N")));
        Assert.IsFalse(body.Contains(removedAndTold.Id.ToString("N")));
        Assert.IsFalse(body.Contains(someoneElses.Id.ToString("N")));
    }

    [TestMethod]
    public async Task BuildFeedAsync_ShowsAnEditedShiftAsPublished_UntilTheEditIsPublished()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        RotaService rota = GetRotaService(options);
        LocationScope scope = SchedulingTestHelpers.AdminScope;

        Shift shift = await SeedShiftAsync(options, location, ben, Monday, 9, 17, published: false);
        await rota.PublishRangeAsync(scope, location.Id, Monday, Monday.AddDays(6), Manager);

        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        Shift edit = new()
        {
            Id = shift.Id,
            LocationId = location.Id,
            UserId = ben.Id,
            StartUtc = RotaTime.ToUtc(Monday, new TimeOnly(12, 0)),
            EndUtc = RotaTime.ToUtc(Monday, new TimeOnly(20, 0)),
            BreakMinutes = 30,
            CreateByUserId = Manager
        };
        Assert.IsTrue((await rota.SaveShiftAsync(scope, edit, Manager)).IsSuccess);

        string beforePublish = await FeedBodyAsync(service, token);
        StringAssert.Contains(beforePublish, "DTSTART:20261005T080000Z");
        Assert.IsFalse(beforePublish.Contains("break"), "The unpublished break mustn't show either.");

        await rota.PublishRangeAsync(scope, location.Id, Monday, Monday.AddDays(6), Manager);

        string afterPublish = await FeedBodyAsync(service, token);
        StringAssert.Contains(afterPublish, "DTSTART:20261005T110000Z");
        StringAssert.Contains(afterPublish, "DTEND:20261005T190000Z");
        StringAssert.Contains(afterPublish, "30 min break");
    }

    [TestMethod]
    public async Task BuildFeedAsync_OnlyIncludesTheTokensCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = ben.Id, LocationId = otherLocation.Id, CreateDate = Now });
            await ctx.SaveChangesAsync();
        }

        Shift here = await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        Shift there = await SeedShiftAsync(options, otherLocation, ben, Monday.AddDays(1), 9, 17);

        CalendarFeedService service = GetService(options);
        string body = await FeedBodyAsync(service, await CreateTokenAsync(service, ben, company));

        StringAssert.Contains(body, here.Id.ToString("N"));
        Assert.IsFalse(body.Contains(there.Id.ToString("N")));
    }

    [TestMethod]
    public async Task BuildFeedAsync_IsEmptyOnceThePersonLeavesTheLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(0, EventCount(await FeedBodyAsync(service, token)));
    }

    [TestMethod]
    public async Task BuildFeedAsync_IsEmptyWhileThePersonIsLockedOut()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = await ctx.Users.SingleAsync(x => x.Id == ben.Id);
            user.LockoutEnd = Now.AddYears(100);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(0, EventCount(await FeedBodyAsync(service, token)));
    }

    [TestMethod]
    public async Task BuildFeedAsync_IsEmptyButStillWorks_WhenShiftManagementIsTurnedOff()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, location, ben, Monday, 9, 17);
        CalendarFeedService service = GetService(options);
        string token = await CreateTokenAsync(service, ben, company);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = company.Id, Feature = CompanyFeature.StaffScheduling, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(0, EventCount(await FeedBodyAsync(service, token)));

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyFeatureSettings.RemoveRange(ctx.CompanyFeatureSettings);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(1, EventCount(await FeedBodyAsync(service, token)));
    }

    [TestMethod]
    public async Task BuildFeedAsync_CoversAMonthBackAndSixMonthsAhead()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        DateOnly today = DateOnly.FromDateTime(Now);

        Shift lastMonth = await SeedShiftAsync(options, location, ben, today.AddDays(-25), 9, 17);
        Shift tooOld = await SeedShiftAsync(options, location, ben, today.AddDays(-40), 9, 17);
        Shift tooFar = await SeedShiftAsync(options, location, ben, today.AddDays(200), 9, 17);

        CalendarFeedService service = GetService(options);
        string body = await FeedBodyAsync(service, await CreateTokenAsync(service, ben, company));

        StringAssert.Contains(body, lastMonth.Id.ToString("N"));
        Assert.IsFalse(body.Contains(tooOld.Id.ToString("N")));
        Assert.IsFalse(body.Contains(tooFar.Id.ToString("N")));
    }

    [TestMethod]
    public async Task BuildFeedAsync_RecordsUseAtMostHourly()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TestClock clock = new(Now);
        CalendarFeedService service = GetService(options, clock);
        string token = await CreateTokenAsync(service, ben, company);

        async Task<DateTime?> LastAccessAsync()
        {
            await using ApplicationDbContext ctx = new(options);
            return (await ctx.CalendarFeedTokens.SingleAsync()).LastAccessUtc;
        }

        await service.BuildFeedAsync(token, null);
        Assert.AreEqual(Now, await LastAccessAsync());

        clock.Advance(TimeSpan.FromMinutes(30));
        await service.BuildFeedAsync(token, null);
        Assert.AreEqual(Now, await LastAccessAsync());

        clock.Advance(TimeSpan.FromMinutes(31));
        await service.BuildFeedAsync(token, null);
        Assert.AreEqual(clock.UtcNow, await LastAccessAsync());
    }
}
