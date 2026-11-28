using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Checklists;

[TestClass]
public class ChecklistServiceTests
{
    private const string ManagerId = "manager-user";

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Ipswich, Location Wisbech, StaffPosition DutyManager)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    private static async Task<World> SeedAsync()
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

        StaffPosition duty = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Duty Manager");

        return new World(options, company, ipswich, wisbech, duty);
    }

    private static ChecklistService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Moq.Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        return new ChecklistService(factory.Object, NullLogger<ChecklistService>.Instance);
    }

    private static Checklist Draft(Location location, string name = " Opening ") => new()
    {
        CompanyId = 0,
        LocationId = location.Id,
        Name = name,
        Days = ChecklistDays.EveryDay,
        DueRule = ChecklistDueRule.AfterOpening,
        DueOffsetMinutes = 15,
        // Ignored for an opening rule.
        DueTime = new TimeOnly(9, 0)
    };

    private static ChecklistTask Task(Guid checklistId, string title) => new() { ChecklistId = checklistId, Title = title };

    [TestMethod]
    public async Task SaveChecklistAsync_New_SavedUnderTheLocationsCompany_Trimmed()
    {
        World w = await SeedAsync();

        Result<Checklist> saved = await GetService(w.Options).SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        await using ApplicationDbContext ctx = new(w.Options);
        Checklist stored = await ctx.Checklists.SingleAsync();
        Assert.AreEqual(w.Company.Id, stored.CompanyId);
        Assert.AreEqual(w.Ipswich.Id, stored.LocationId);
        Assert.AreEqual("Opening", stored.Name);
        Assert.AreEqual(15, stored.DueOffsetMinutes);
        Assert.IsNull(stored.DueTime);
        Assert.AreEqual(ManagerId, stored.CreateByUserId);
    }

    [TestMethod]
    public async Task SaveChecklistAsync_ManagerOfAnotherLocation_Refused_AdminAllowed()
    {
        World w = await SeedAsync();

        Result<Checklist> manager = await GetService(w.Options).SaveChecklistAsync(w.ManagerScope, Draft(w.Wisbech), ManagerId);
        Result<Checklist> staff = await GetService(w.Options).SaveChecklistAsync(SchedulingTestHelpers.StaffScopeFor(w.Ipswich), Draft(w.Ipswich), ManagerId);
        Result<Checklist> admin = await GetService(w.Options).SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Wisbech), ManagerId);

        Assert.IsFalse(manager.IsSuccess);
        Assert.IsFalse(staff.IsSuccess);
        Assert.IsTrue(admin.IsSuccess, admin.Error);
    }

    [TestMethod]
    public async Task SaveChecklistAsync_Invalid_Refused()
    {
        World w = await SeedAsync();
        Checklist noDays = Draft(w.Ipswich);
        noDays.Days = ChecklistDays.None;

        Result<Checklist> result = await GetService(w.Options).SaveChecklistAsync(w.ManagerScope, noDays, ManagerId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Choose at least one day.", result.Error);
    }

    [TestMethod]
    public async Task SaveChecklistAsync_Position_MustBeTheCompanysAndActive()
    {
        World w = await SeedAsync();
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Partyman", "Peterborough");
        StaffPosition foreign = await SchedulingTestHelpers.SeedPositionAsync(w.Options, other, "Host");

        Checklist withForeign = Draft(w.Ipswich);
        withForeign.StaffPositionId = foreign.Id;
        Checklist withOwn = Draft(w.Ipswich);
        withOwn.StaffPositionId = w.DutyManager.Id;

        Assert.IsFalse((await GetService(w.Options).SaveChecklistAsync(w.ManagerScope, withForeign, ManagerId)).IsSuccess);
        Assert.IsTrue((await GetService(w.Options).SaveChecklistAsync(w.ManagerScope, withOwn, ManagerId)).IsSuccess);
    }

    [TestMethod]
    public async Task SaveChecklistAsync_ArchivedPositionAlreadyOnTheChecklist_StillSaves()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist draft = Draft(w.Ipswich);
        draft.StaffPositionId = w.DutyManager.Id;
        Checklist saved = (await service.SaveChecklistAsync(w.ManagerScope, draft, ManagerId)).Data!;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.StaffPositions.SingleAsync(x => x.Id == w.DutyManager.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Checklist edit = Draft(w.Ipswich, "Opening checks");
        edit.Id = saved.Id;
        edit.StaffPositionId = w.DutyManager.Id;

        Result<Checklist> result = await service.SaveChecklistAsync(w.ManagerScope, edit, ManagerId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Opening checks", result.Data!.Name);
    }

    [TestMethod]
    public async Task SaveChecklistAsync_ExistingIdAtAnotherLocation_NotFound()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist wisbech = (await service.SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Wisbech), ManagerId)).Data!;

        // A Manager at Ipswich posting Wisbech's checklist id as if it were Ipswich's.
        Checklist hijack = Draft(w.Ipswich, "Mine now");
        hijack.Id = wisbech.Id;

        Result<Checklist> result = await service.SaveChecklistAsync(w.ManagerScope, hijack, ManagerId);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual("Opening", (await ctx.Checklists.SingleAsync(x => x.Id == wisbech.Id)).Name);
    }

    [TestMethod]
    public async Task GetChecklistAsync_ManagerOfAnotherLocation_NotFound()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist wisbech = (await service.SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Wisbech), ManagerId)).Data!;

        Assert.IsFalse((await service.GetChecklistAsync(w.ManagerScope, wisbech.Id)).IsSuccess);
        Assert.IsTrue((await service.GetChecklistAsync(SchedulingTestHelpers.AdminScope, wisbech.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task GetChecklistsAsync_HidesArchivedUnlessAsked_AndOnlyActiveTasksInOrder()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist opening = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId)).Data!;
        Checklist closing = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich, "Closing"), ManagerId)).Data!;

        await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "Lights on"), ManagerId);
        ChecklistTask removed = (await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "Old task"), ManagerId)).Data!;
        await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "Tills on"), ManagerId);
        await service.RemoveTaskAsync(w.ManagerScope, removed.Id, ManagerId);
        await service.SetArchivedAsync(w.ManagerScope, closing.Id, true, ManagerId);

        List<Checklist> active = (await service.GetChecklistsAsync(w.ManagerScope, w.Ipswich.Id)).Data!;
        List<Checklist> all = (await service.GetChecklistsAsync(w.ManagerScope, w.Ipswich.Id, includeArchived: true)).Data!;

        Assert.AreEqual(1, active.Count);
        CollectionAssert.AreEqual(new[] { "Lights on", "Tills on" }, active[0].Tasks.Select(x => x.Title).ToArray());
        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public async Task SaveTaskAsync_UnitOnlyKeptForNumberTasks_AndAddsAtTheEnd()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist opening = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId)).Data!;

        ChecklistTask first = (await service.SaveTaskAsync(w.ManagerScope, new ChecklistTask { ChecklistId = opening.Id, Title = "Unlock", Unit = "°C" }, ManagerId)).Data!;
        ChecklistTask second = (await service.SaveTaskAsync(w.ManagerScope,
            new ChecklistTask { ChecklistId = opening.Id, Title = " Fridge ", Response = ChecklistResponse.Number, Unit = " °C " }, ManagerId)).Data!;

        Assert.IsNull(first.Unit);
        Assert.AreEqual("Fridge", second.Title);
        Assert.AreEqual("°C", second.Unit);
        Assert.IsTrue(second.SortOrder > first.SortOrder);
    }

    [TestMethod]
    public async Task SaveTaskAsync_KeepsTheRangeAndAlertSetting_AndClearsTheRangeWhenNoLongerANumber()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist opening = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId)).Data!;

        ChecklistTask fridge = (await service.SaveTaskAsync(w.ManagerScope, new ChecklistTask
        {
            ChecklistId = opening.Id, Title = "Fridge", Response = ChecklistResponse.Number, Unit = "°C",
            MinReading = 0m, MaxReading = 5m, ReadingAlert = ChecklistReadingAlert.AlertOnly
        }, ManagerId)).Data!;

        Assert.AreEqual(0m, fridge.MinReading);
        Assert.AreEqual(5m, fridge.MaxReading);
        Assert.AreEqual(ChecklistReadingAlert.AlertOnly, fridge.ReadingAlert);

        // Saving it with the range left blank and "Don't alert" stores exactly that (None is the
        // enum's zero, so it must not fall back to a database default).
        Result<ChecklistTask> open = await service.SaveTaskAsync(w.ManagerScope, new ChecklistTask
        {
            Id = fridge.Id, ChecklistId = opening.Id, Title = "Fridge", Response = ChecklistResponse.Number, MaxReading = 8m,
            ReadingAlert = ChecklistReadingAlert.None
        }, ManagerId);
        Assert.IsTrue(open.IsSuccess, open.Error);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistTask stored = await ctx.ChecklistTasks.IgnoreQueryFilters().SingleAsync(x => x.Id == fridge.Id);
            Assert.IsNull(stored.MinReading);
            Assert.AreEqual(8m, stored.MaxReading);
            Assert.AreEqual(ChecklistReadingAlert.None, stored.ReadingAlert);
        }

        // A range on a plain tick is refused rather than silently kept.
        Result<ChecklistTask> tick = await service.SaveTaskAsync(w.ManagerScope, new ChecklistTask
        {
            Id = fridge.Id, ChecklistId = opening.Id, Title = "Fridge", Response = ChecklistResponse.Tick, MaxReading = 8m
        }, ManagerId);
        Assert.IsFalse(tick.IsSuccess);

        Result<ChecklistTask> plain = await service.SaveTaskAsync(w.ManagerScope, new ChecklistTask
        {
            Id = fridge.Id, ChecklistId = opening.Id, Title = "Fridge", Response = ChecklistResponse.Tick
        }, ManagerId);
        Assert.IsTrue(plain.IsSuccess, plain.Error);
        Assert.IsNull(plain.Data!.MaxReading);
    }

    [TestMethod]
    public async Task SaveTaskAsync_OnAnotherLocationsChecklist_Refused()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist wisbech = (await service.SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Wisbech), ManagerId)).Data!;

        Result<ChecklistTask> result = await service.SaveTaskAsync(w.ManagerScope, Task(wisbech.Id, "Sneaky"), ManagerId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTaskAsync_StopsAtTheMaximum()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist opening = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId)).Data!;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.ChecklistTasks.AddRange(Enumerable.Range(0, ChecklistRules.MaxTasks)
                .Select(i => new ChecklistTask { ChecklistId = opening.Id, Title = $"Task {i}", SortOrder = i }));
            await ctx.SaveChangesAsync();
        }

        Result<ChecklistTask> result = await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "One too many"), ManagerId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task MoveTaskAsync_SwapsWithTheNeighbour_AndStopsAtTheEnds()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist opening = (await service.SaveChecklistAsync(w.ManagerScope, Draft(w.Ipswich), ManagerId)).Data!;
        ChecklistTask a = (await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "A"), ManagerId)).Data!;
        await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "B"), ManagerId);
        ChecklistTask c = (await service.SaveTaskAsync(w.ManagerScope, Task(opening.Id, "C"), ManagerId)).Data!;

        Assert.IsTrue((await service.MoveTaskAsync(w.ManagerScope, c.Id, -1, ManagerId)).Data);
        Assert.IsFalse((await service.MoveTaskAsync(w.ManagerScope, a.Id, -1, ManagerId)).Data);
        Assert.IsFalse((await service.MoveTaskAsync(w.ManagerScope, a.Id, 5, ManagerId)).IsSuccess);

        List<string> order = (await service.GetChecklistAsync(w.ManagerScope, opening.Id)).Data!.Tasks.Select(x => x.Title).ToList();
        CollectionAssert.AreEqual(new[] { "A", "C", "B" }, order);
    }

    // A signed-in caller, so the company query filter applies.
    private sealed class SignedInTenant(int companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }

    [TestMethod]
    public async Task AdminOfAnotherCompany_CantSeeOrChangeTheChecklist()
    {
        World w = await SeedAsync();
        Checklist opening = (await GetService(w.Options).SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Ipswich), ManagerId)).Data!;
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Partyman", "Peterborough");

        Moq.Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(w.Options, new SignedInTenant(other.Id)));
        ChecklistService otherCompany = new(factory.Object, NullLogger<ChecklistService>.Instance);

        Assert.IsFalse((await otherCompany.GetChecklistAsync(SchedulingTestHelpers.AdminScope, opening.Id)).IsSuccess);
        Assert.AreEqual(0, (await otherCompany.GetChecklistsAsync(SchedulingTestHelpers.AdminScope, w.Ipswich.Id)).Data!.Count);
        Assert.IsFalse((await otherCompany.SetArchivedAsync(SchedulingTestHelpers.AdminScope, opening.Id, true, ManagerId)).IsSuccess);
        Assert.IsFalse((await otherCompany.SaveTaskAsync(SchedulingTestHelpers.AdminScope, Task(opening.Id, "Sneaky"), ManagerId)).IsSuccess);
    }

    [TestMethod]
    public async Task SetArchivedAsync_ManagerOfAnotherLocation_Refused()
    {
        World w = await SeedAsync();
        ChecklistService service = GetService(w.Options);
        Checklist wisbech = (await service.SaveChecklistAsync(SchedulingTestHelpers.AdminScope, Draft(w.Wisbech), ManagerId)).Data!;

        Assert.IsFalse((await service.SetArchivedAsync(w.ManagerScope, wisbech.Id, true, ManagerId)).IsSuccess);
        Assert.IsTrue((await service.SetArchivedAsync(SchedulingTestHelpers.AdminScope, wisbech.Id, true, ManagerId)).IsSuccess);
    }
}
