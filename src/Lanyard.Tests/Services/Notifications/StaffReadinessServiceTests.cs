using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Notifications;

[TestClass]
public class StaffReadinessServiceTests
{
    private static StaffReadinessService Service(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new StaffReadinessService(factory.Object, NullLogger<StaffReadinessService>.Instance);
    }

    private static async Task AddInstallAsync(DbContextOptions<ApplicationDbContext> options, string userId, DateTime lastSeen, string deviceId = "phone")
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.AppInstallations.Add(new AppInstallation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = deviceId,
            DeviceLabel = "Lanyard app on iPhone",
            Platform = DevicePlatform.IPhone,
            FirstSeenUtc = lastSeen,
            LastSeenUtc = lastSeen
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task AddPushAsync(DbContextOptions<ApplicationDbContext> options, string userId, string endpoint = "https://push.example/1")
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.PushSubscriptions.Add(new UserPushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Endpoint = endpoint,
            P256dh = "key",
            Auth = "auth",
            DeviceLabel = "Lanyard app on iPhone",
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task CountsInstallsAndPushPerPerson_AndListsNotReadyFirst()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Ben");
        UserProfile cat = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Cat");

        DateTime seen = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        await AddInstallAsync(options, amy.Id, seen, "phone");
        await AddInstallAsync(options, amy.Id, seen.AddDays(-3), "tablet");
        await AddPushAsync(options, amy.Id);
        await AddPushAsync(options, ben.Id, "https://push.example/ben");

        Result<List<StaffReadinessRow>> result = await Service(options).GetAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich));

        Assert.IsTrue(result.IsSuccess, result.Error);
        List<StaffReadinessRow> rows = result.Data!;
        Assert.HasCount(3, rows);

        StaffReadinessRow amyRow = rows.Single(x => x.UserId == amy.Id);
        Assert.AreEqual(2, amyRow.InstalledDevices);
        Assert.AreEqual(1, amyRow.PushDevices);
        Assert.AreEqual(seen, amyRow.LastAppUseUtc);
        Assert.IsTrue(amyRow.IsReady);

        StaffReadinessRow benRow = rows.Single(x => x.UserId == ben.Id);
        Assert.IsFalse(benRow.HasApp);
        Assert.IsTrue(benRow.HasNotifications);
        Assert.IsFalse(benRow.IsReady, "Notifications alone, without the app, isn't ready");

        StaffReadinessRow catRow = rows.Single(x => x.UserId == cat.Id);
        Assert.AreEqual(0, catRow.InstalledDevices);
        Assert.IsNull(catRow.LastAppUseUtc);

        Assert.AreEqual(amy.Id, rows[^1].UserId, "Ready people sort to the end");
        Assert.AreEqual("Ben Tester", rows[0].Name);
    }

    [TestMethod]
    public async Task OnlyListsPeopleAtTheScopedLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location wisbech;
        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Wes");

        Result<List<StaffReadinessRow>> result = await Service(options).GetAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(amy.Id, result.Data![0].UserId);
    }

    [TestMethod]
    public async Task IgnoresALocationInAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location other) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        await SchedulingTestHelpers.SeedUserAsync(options, other, "Ola");

        // A manager's scope for Ipswich's company, pointed at the other company's location.
        LocationScope crossed = new(false, other.Id, ipswich.CompanyId, "Elsewhere", IsManager: true);

        Result<List<StaffReadinessRow>> result = await Service(options).GetAsync(crossed);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task LeavesOutTheDeletedUserPlaceholder()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted-user", FirstName = "Deleted", LastName = "User" });
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = ApplicationDbContext.SystemDeletedUserPlaceholderId, LocationId = ipswich.Id });
            await ctx.SaveChangesAsync();
        }

        Result<List<StaffReadinessRow>> result = await Service(options).GetAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task StaffCannotSeeIt()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<List<StaffReadinessRow>> result = await Service(options).GetAsync(SchedulingTestHelpers.StaffScopeFor(ipswich));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task NeedsALocation()
    {
        Result<List<StaffReadinessRow>> result = await Service(SchedulingTestHelpers.GetInMemoryOptions())
            .GetAsync(SchedulingTestHelpers.AdminScope);

        Assert.IsFalse(result.IsSuccess);
    }
}
