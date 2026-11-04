#nullable enable

using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Lanyard.Tests.Services.Automation;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

[TestClass]
public class LocationOpeningHoursServiceTests
{
    private const int OwnCompanyId = 1;
    private const int OtherCompanyId = 2;

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ISystemDbContextFactory GetSystemFactory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    private static AutomationEngineService GetEngine(DbContextOptions<ApplicationDbContext> options, RecordingActionExecutor? executor = null, TimeProvider? clock = null)
    {
        return new AutomationEngineService(
            GetSystemFactory(options),
            executor is null ? [] : [executor],
            new LaserGameStatusStore(),
            clock ?? TimeProvider.System,
            NullLogger<AutomationEngineService>.Instance);
    }

    // tenantCompanyId: the signed-in company the contexts are filtered to (null = system).
    private static LocationOpeningHoursService GetService(
        DbContextOptions<ApplicationDbContext> options,
        AutomationEngineService engine,
        ITenantContext? tenant = null,
        int? tenantCompanyId = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => tenantCompanyId is int companyId
                ? new ApplicationDbContext(options, new FixedTenantProvider(companyId))
                : new ApplicationDbContext(options));

        return new LocationOpeningHoursService(factoryMock.Object, engine, NullLogger<LocationOpeningHoursService>.Instance, tenant);
    }

    private static ITenantContext CompanyAdmin(int companyId)
    {
        Mock<ITenantContext> tenant = new();
        tenant.Setup(t => t.CompanyId).Returns(companyId);
        tenant.Setup(t => t.ManageableCompanyId).Returns(companyId);
        tenant.Setup(t => t.CanManageCompany(It.IsAny<int>())).Returns((int id) => id == companyId);

        return tenant.Object;
    }

    private static ITenantContext PlatformAdmin(int signedInCompanyId)
    {
        Mock<ITenantContext> tenant = new();
        tenant.Setup(t => t.CompanyId).Returns(signedInCompanyId);
        tenant.Setup(t => t.IsPlatformAdmin).Returns(true);
        tenant.Setup(t => t.ManageableCompanyId).Returns((int?)null);
        tenant.Setup(t => t.CanManageCompany(It.IsAny<int>())).Returns(true);

        return tenant.Object;
    }

    private static async Task<int> SeedLocationAsync(DbContextOptions<ApplicationDbContext> options, int companyId, string name = "Ipswich", bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(options);

        Location location = new() { CompanyId = companyId, Name = name, IsActive = isActive, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return location.Id;
    }

    private static List<OpeningHoursWindow> Weekdays(TimeOnly opensAt, TimeOnly closesAt)
    {
        return
        [
            new(DayOfWeek.Monday, opensAt, closesAt),
            new(DayOfWeek.Tuesday, opensAt, closesAt),
            new(DayOfWeek.Wednesday, opensAt, closesAt),
            new(DayOfWeek.Thursday, opensAt, closesAt),
            new(DayOfWeek.Friday, opensAt, closesAt)
        ];
    }

    [TestMethod]
    public async Task GetOpeningHours_IsEmpty_WhenNoneAreSet()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Result<List<OpeningHoursWindow>> result = await service.GetOpeningHoursAsync(locationId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task SaveOpeningHours_RoundTrips_MondayFirst_AndAllowsSplitDays()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Result<bool> saved = await service.SaveOpeningHoursAsync(locationId,
        [
            new(DayOfWeek.Sunday, new(10, 0), new(16, 0)),
            new(DayOfWeek.Monday, new(17, 0), new(22, 0)),
            new(DayOfWeek.Monday, new(11, 0), new(14, 0))
        ]);

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        List<OpeningHoursWindow> windows = (await service.GetOpeningHoursAsync(locationId)).Data!;
        CollectionAssert.AreEqual(
            new List<OpeningHoursWindow>
            {
                new(DayOfWeek.Monday, new(11, 0), new(14, 0)),
                new(DayOfWeek.Monday, new(17, 0), new(22, 0)),
                new(DayOfWeek.Sunday, new(10, 0), new(16, 0))
            },
            windows);
    }

    [TestMethod]
    public async Task SaveOpeningHours_KeepsUnchangedWindows_AndSoftDeletesRemovedOnes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        await service.SaveOpeningHoursAsync(locationId, Weekdays(new(10, 0), new(22, 0)));

        int mondayRowId;
        await using (ApplicationDbContext ctx = new(options))
        {
            mondayRowId = await ctx.LocationOpeningHours.Where(h => h.DayOfWeek == DayOfWeek.Monday).Select(h => h.Id).SingleAsync();
        }

        // Friday changes, Tuesday is removed, the rest stay.
        List<OpeningHoursWindow> next = [.. Weekdays(new(10, 0), new(22, 0)).Where(w => w.DayOfWeek is not (DayOfWeek.Tuesday or DayOfWeek.Friday))];
        next.Add(new(DayOfWeek.Friday, new(10, 0), new(23, 0)));
        Result<bool> second = await service.SaveOpeningHoursAsync(locationId, next);

        Assert.IsTrue(second.IsSuccess, second.Error);

        await using (ApplicationDbContext ctx = new(options))
        {
            List<LocationOpeningHours> rows = await ctx.LocationOpeningHours.ToListAsync();

            Assert.HasCount(6, rows, "Five original rows plus the new Friday; nothing is hard-deleted.");
            Assert.AreEqual(4, rows.Count(r => r.IsActive));
            Assert.IsTrue(rows.Single(r => r.Id == mondayRowId).IsActive, "An unchanged window keeps its row.");
            Assert.IsFalse(rows.Single(r => r.DayOfWeek == DayOfWeek.Tuesday).IsActive);
        }
    }

    [TestMethod]
    public async Task SaveOpeningHours_RejectsTheSameOpenAndCloseTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Result<bool> result = await service.SaveOpeningHoursAsync(locationId, [new(DayOfWeek.Friday, new(10, 0), new(10, 0))]);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Friday: opening and closing times can't be the same.", result.Error);
    }

    [TestMethod]
    public async Task SaveOpeningHours_RejectsOverlappingWindows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Result<bool> result = await service.SaveOpeningHoursAsync(locationId,
        [
            new(DayOfWeek.Saturday, new(18, 0), new(2, 0)),
            new(DayOfWeek.Sunday, new(1, 0), new(6, 0))
        ]);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Sunday: the hours starting at 18:00 and 01:00 overlap.", result.Error);
    }

    [TestMethod]
    public async Task SaveOpeningHours_AllowsHoursThatRunPastMidnight()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Result<bool> result = await service.SaveOpeningHoursAsync(locationId, [new(DayOfWeek.Saturday, new(18, 0), new(2, 0))]);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task OpeningHours_FailForADeactivatedLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId, isActive: false);
        LocationOpeningHoursService service = GetService(options, GetEngine(options));

        Assert.AreEqual("Location not found.", (await service.GetOpeningHoursAsync(locationId)).Error);
        Assert.AreEqual("Location not found.", (await service.SaveOpeningHoursAsync(locationId, Weekdays(new(10, 0), new(20, 0)))).Error);
    }

    [TestMethod]
    public async Task OpeningHours_RefuseAnotherCompanysLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int otherLocationId = await SeedLocationAsync(options, OtherCompanyId, "Elsewhere");
        LocationOpeningHoursService service = GetService(options, GetEngine(options), CompanyAdmin(OwnCompanyId), OwnCompanyId);

        Result<List<OpeningHoursWindow>> read = await service.GetOpeningHoursAsync(otherLocationId);
        Result<bool> write = await service.SaveOpeningHoursAsync(otherLocationId, Weekdays(new(10, 0), new(20, 0)));

        Assert.IsFalse(read.IsSuccess);
        Assert.IsFalse(write.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.LocationOpeningHours.CountAsync());
    }

    [TestMethod]
    public async Task OpeningHours_LetAPlatformAdminManageAnotherCompanysLocation()
    {
        // A platform admin signed in to company 1 editing company 2's location. Their contexts are
        // filtered to company 1, which would hide company 2's rows from the second save, leaving
        // the first save's windows active alongside the new ones if the service relied on the filter.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int otherLocationId = await SeedLocationAsync(options, OtherCompanyId, "Elsewhere");
        LocationOpeningHoursService service = GetService(options, GetEngine(options), PlatformAdmin(OwnCompanyId), OwnCompanyId);

        Assert.IsTrue((await service.SaveOpeningHoursAsync(otherLocationId, Weekdays(new(10, 0), new(20, 0)))).IsSuccess);
        Result<bool> second = await service.SaveOpeningHoursAsync(otherLocationId, Weekdays(new(11, 0), new(20, 0)));

        Assert.IsTrue(second.IsSuccess, second.Error);
        List<OpeningHoursWindow> windows = (await service.GetOpeningHoursAsync(otherLocationId)).Data!;
        Assert.HasCount(5, windows, "The first save's windows must be replaced, not left alongside the new ones.");
        Assert.AreEqual(new TimeOnly(11, 0), windows[0].OpensAt);
    }

    [TestMethod]
    public async Task SaveOpeningHours_ClearsTheEngineCache_SoRulesSeeNewHoursStraightAway()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedLocationAsync(options, OwnCompanyId);
        Guid kioskId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "AutomationEngine.Enabled", Value = "true", CreateDate = DateTime.UtcNow });
            ctx.AutomationRules.Add(new AutomationRule
            {
                Id = Guid.NewGuid(),
                CompanyId = OwnCompanyId,
                Name = "Only while open",
                TriggerClientId = kioskId,
                TriggerType = AutomationTriggerType.GameStatusTransition,
                TriggerEvent = GameStatus.InGame,
                IsActive = true,
                IsEnabled = true,
                CreateDate = DateTime.UtcNow,
                Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, IsActive = true }],
                Conditions =
                [
                    new AutomationRuleCondition
                    {
                        Id = Guid.NewGuid(),
                        ConditionType = AutomationConditionType.DuringOpeningHours,
                        ParametersJson = AutomationConditionParameters.Serialize(new OpeningHoursConditionParameters(locationId, true)),
                        IsActive = true
                    }
                ]
            });
            await ctx.SaveChangesAsync();
        }

        // 15:00 UTC on Wednesday 15 July is 16:00 in London.
        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor, new TestClock(new DateTime(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc)));
        LocationOpeningHoursService service = GetService(options, engine);
        GameStatusTransitionEvent gameStarted = new(kioskId, GameStatus.NotStarted, GameStatus.InGame);

        await engine.ProcessTransitionAsync(gameStarted, CancellationToken.None);
        Assert.IsEmpty(executor.ExecutedActionIds, "No hours set yet, so the location counts as closed.");

        await service.SaveOpeningHoursAsync(locationId, Weekdays(new(10, 0), new(22, 0)));
        await engine.ProcessTransitionAsync(gameStarted, CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds, "Saving hours must clear the engine's cache so the rule sees them.");
    }
}
