using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.OperationsAlerts;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.OperationsAlerts;

[TestClass]
public class OperationsAlertServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private sealed class World
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required Company Company { get; init; }
        public required Location Ipswich { get; init; }
        public required Location Wisbech { get; init; }
        public required UserProfile Admin { get; init; }
        public required UserProfile Manager { get; init; }
        public List<OperationsAlertChanged> Events { get; } = [];
    }

    private static async Task<World> BuildAsync()
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

        return new World
        {
            Options = options,
            Company = company,
            Ipswich = ipswich,
            Wisbech = wisbech,
            Admin = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Ada"),
            Manager = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Mia")
        };
    }

    private static OperationsAlertService ServiceFor(World world, LocationScope scope, string userId, int? companyId = -1, bool isSystem = false)
    {
        Mock<ITenantContext> tenant = new();
        tenant.SetupGet(x => x.IsSystem).Returns(isSystem);
        tenant.SetupGet(x => x.CompanyId).Returns(companyId == -1 ? world.Company.Id : companyId);

        Mock<ICurrentLocationContext> location = new();
        location.Setup(x => x.GetScopeAsync()).ReturnsAsync(Result<LocationScope>.Ok(scope));

        Mock<ICurrentUserAccessor> user = new();
        user.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(userId));

        OperationsAlertEventBus bus = new();
        bus.OnChanged += world.Events.Add;

        return new OperationsAlertService(SchedulingTestHelpers.GetFactory(world.Options), tenant.Object, location.Object, user.Object,
            bus, new TestClock(Now), NullLogger<OperationsAlertService>.Instance);
    }

    private static OperationsAlertService AdminService(World w) => ServiceFor(w, SchedulingTestHelpers.AdminScope, w.Admin.Id);

    private static OperationsAlertService ManagerService(World w) => ServiceFor(w, SchedulingTestHelpers.ManagerScopeFor(w.Ipswich), w.Manager.Id);

    [TestMethod]
    public async Task Admin_SeesEveryLocationAndCompanyWideAlertsButNotOtherCompanies()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Wisbech.Id, "b");
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, null, "c");
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id + 50, null, "other-company");

        Result<List<OperationsAlertView>> open = await AdminService(w).GetOpenAsync(new OperationsAlertQuery());

        CollectionAssert.AreEquivalent(new[] { "Alert a", "Alert b", "Alert c" }, open.Data!.Select(x => x.Title).ToList());
    }

    [TestMethod]
    public async Task Manager_SeesOnlyTheirOwnLocation()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Wisbech.Id, "b");
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, null, "c");

        Result<List<OperationsAlertView>> open = await ManagerService(w).GetOpenAsync(new OperationsAlertQuery(LocationId: w.Wisbech.Id));

        Assert.AreEqual("Alert a", open.Data!.Single().Title, "and asking for another location doesn't widen it");
    }

    [TestMethod]
    public async Task Open_IsMostSevereFirstAndLeavesResolvedOut()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "warn", raisedAt: Now.AddMinutes(-1));
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "crit", severity: OperationsAlertSeverity.Critical, raisedAt: Now.AddHours(-2));
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "done", OperationsAlertStatus.Resolved, resolvedAt: Now);

        Result<List<OperationsAlertView>> open = await AdminService(w).GetOpenAsync(new OperationsAlertQuery());

        CollectionAssert.AreEqual(new[] { "Alert crit", "Alert warn" }, open.Data!.Select(x => x.Title).ToList());
    }

    [TestMethod]
    public async Task History_ListsResolvedAlertsFromTheLast90DaysNewestFirst()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "old", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-91));
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "week", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-7));
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "today", OperationsAlertStatus.Resolved, resolvedAt: Now.AddHours(-1));
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "open");

        Result<OperationsAlertHistoryPage> history = await AdminService(w).GetHistoryAsync(new OperationsAlertQuery(), 0, 1);

        Assert.AreEqual(2, history.Data!.TotalCount);
        Assert.AreEqual("Alert today", history.Data.Alerts.Single().Title);
    }

    [TestMethod]
    public async Task Counts_SplitOpenAcknowledgedAndCritical()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a", severity: OperationsAlertSeverity.Critical);
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "b", OperationsAlertStatus.Acknowledged);
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Wisbech.Id, "c");

        OperationsAlertCounts counts = (await ManagerService(w).GetCountsAsync()).Data!;

        Assert.AreEqual(new OperationsAlertCounts(Open: 1, Acknowledged: 1, Critical: 1), counts);
    }

    [TestMethod]
    public async Task Acknowledge_RecordsWhoAndWhenAndTellsOpenPages()
    {
        World w = await BuildAsync();
        OperationsAlert alert = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");

        Result<bool> result = await ManagerService(w).AcknowledgeAsync(alert.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        OperationsAlertView view = (await ManagerService(w).GetOpenAsync(new OperationsAlertQuery())).Data!.Single();
        Assert.AreEqual(OperationsAlertStatus.Acknowledged, view.Status);
        Assert.AreEqual("Mia Tester", view.AcknowledgedByName);
        Assert.AreEqual(Now, view.AcknowledgedAtUtc);
        Assert.AreEqual(OperationsAlertChangeKind.Acknowledged, w.Events.Single().Kind);

        Assert.IsFalse((await ManagerService(w).AcknowledgeAsync(alert.Id)).IsSuccess, "already acknowledged");
    }

    [TestMethod]
    public async Task Resolve_KeepsTheNoteAndMovesTheAlertToHistory()
    {
        World w = await BuildAsync();
        OperationsAlert alert = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");

        Result<bool> result = await ManagerService(w).ResolveAsync(alert.Id, "  Restarted the PC.  ");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty((await ManagerService(w).GetOpenAsync(new OperationsAlertQuery())).Data!);

        OperationsAlertView resolved = (await ManagerService(w).GetHistoryAsync(new OperationsAlertQuery(), 0, 10)).Data!.Alerts.Single();
        Assert.AreEqual(OperationsAlertResolution.Manual, resolved.Resolution);
        Assert.AreEqual("Mia Tester", resolved.ResolvedByName);
        Assert.AreEqual("Restarted the PC.", resolved.ResolutionNote);
    }

    [TestMethod]
    public async Task Resolve_RefusesATooLongNote()
    {
        World w = await BuildAsync();
        OperationsAlert alert = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");

        Result<bool> result = await ManagerService(w).ResolveAsync(alert.Id, new string('x', OperationsAlert.MaxNoteLength + 1));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Manager_CannotActOnAnotherLocationsOrACompanyWideAlert()
    {
        World w = await BuildAsync();
        OperationsAlert elsewhere = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Wisbech.Id, "a");
        OperationsAlert companyWide = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, null, "b");

        Assert.AreEqual("That alert couldn't be found.", (await ManagerService(w).AcknowledgeAsync(elsewhere.Id)).Error);
        Assert.AreEqual("That alert couldn't be found.", (await ManagerService(w).ResolveAsync(companyWide.Id, null)).Error);
        Assert.IsTrue((await AdminService(w).ResolveAsync(companyWide.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task Admin_CannotActOnAnotherCompanysAlert()
    {
        World w = await BuildAsync();
        OperationsAlert other = await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id + 50, null, "a");

        Assert.IsFalse((await AdminService(w).AcknowledgeAsync(other.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task SignedOutKiosk_IsRefusedBeforeAnyQuery()
    {
        World w = await BuildAsync();
        await OperationsAlertTestSupport.SeedAlertAsync(w.Options, w.Company.Id, w.Ipswich.Id, "a");

        OperationsAlertService system = ServiceFor(w, SchedulingTestHelpers.AdminScope, w.Admin.Id, companyId: null, isSystem: true);

        Assert.IsFalse((await system.GetOpenAsync(new OperationsAlertQuery())).IsSuccess);
        Assert.IsFalse((await system.GetCountsAsync()).IsSuccess);
    }

    [TestMethod]
    public async Task Staff_AreRefused()
    {
        World w = await BuildAsync();

        OperationsAlertService staff = ServiceFor(w, SchedulingTestHelpers.StaffScopeFor(w.Ipswich), w.Manager.Id);

        Assert.IsFalse((await staff.GetOpenAsync(new OperationsAlertQuery())).IsSuccess);
    }

    [TestMethod]
    public async Task Viewer_CanSeeFollowsTheSameRulesForLiveUpdates()
    {
        World w = await BuildAsync();
        OperationsAlertViewer manager = (await ManagerService(w).GetViewerAsync()).Data!;
        OperationsAlertViewer admin = (await AdminService(w).GetViewerAsync()).Data!;

        OperationsAlertChanged Change(int companyId, int? locationId) =>
            new(companyId, locationId, Guid.NewGuid(), OperationsAlertChangeKind.Raised, OperationsAlertSeverity.Warning, "x");

        Assert.IsTrue(manager.CanSee(Change(w.Company.Id, w.Ipswich.Id)));
        Assert.IsFalse(manager.CanSee(Change(w.Company.Id, w.Wisbech.Id)));
        Assert.IsFalse(manager.CanSee(Change(w.Company.Id, null)));
        Assert.IsTrue(admin.CanSee(Change(w.Company.Id, null)));
        Assert.IsFalse(admin.CanSee(Change(w.Company.Id + 1, null)));
    }
}
