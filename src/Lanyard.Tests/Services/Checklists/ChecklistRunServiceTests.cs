using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Checklists;

[TestClass]
public class ChecklistRunServiceTests
{
    // Saturday 21 Nov 2026, 10:00 GMT - the venue's "today".
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Ipswich,
        Location Wisbech,
        UserProfile Amy,
        UserProfile Tom,
        StaffPosition DutyManager,
        Checklist Opening,
        TestClock Clock)
    {
        public LocationScope AmyScope => SchedulingTestHelpers.StaffScopeFor(Ipswich);
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

        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Tom");
        StaffPosition duty = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Duty Manager");

        Checklist opening = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            LocationId = ipswich.Id,
            Name = "Opening",
            DueRule = ChecklistDueRule.FixedTime,
            DueTime = new TimeOnly(11, 0),
            CreateDate = Now.AddDays(-20),
            Tasks =
            [
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Unlock", SortOrder = 0 },
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Fridge", SortOrder = 1, Response = ChecklistResponse.Number, Unit = "°C" },
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Walk round", SortOrder = 2, Response = ChecklistResponse.TickWithNote }
            ]
        };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Checklists.Add(opening);

            // Amy is on today's rota at Ipswich; Tom isn't on at all.
            ctx.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                LocationId = ipswich.Id,
                UserId = amy.Id,
                StartUtc = Now.AddHours(-1),
                EndUtc = Now.AddHours(6),
                IsActive = true,
                PublishedDateUtc = Now.AddDays(-3),
                CreateByUserId = "manager"
            });

            await ctx.SaveChangesAsync();
        }

        return new World(options, company, ipswich, wisbech, amy, tom, duty, opening, new TestClock(Now));
    }

    private static ChecklistRunService GetService(World w)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(w.Options));

        return new ChecklistRunService(factory.Object, w.Clock, NullLogger<ChecklistRunService>.Instance);
    }

    private static async Task<ChecklistRunView> TodayAsync(World w, UserProfile user, LocationScope scope)
    {
        Result<ChecklistDayView> day = await GetService(w).GetMyTodayAsync(user.Id, scope);
        Assert.IsTrue(day.IsSuccess, day.Error);

        return day.Data!.Runs.Single();
    }

    private static Guid Item(ChecklistRunView run, string title) => run.Items.Single(x => x.Title == title).ItemId;

    [TestMethod]
    public async Task GetMyTodayAsync_MakesTodaysRunFromTheTasks_InOrder_WithItsDueTime()
    {
        World w = await SeedAsync();

        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);

        CollectionAssert.AreEqual(new[] { "Unlock", "Fridge", "Walk round" }, run.Items.Select(x => x.Title).ToArray());
        Assert.AreEqual(new DateTime(2026, 11, 21, 11, 0, 0, DateTimeKind.Utc), run.DueUtc);
        Assert.AreEqual(ChecklistRunStatus.NotStarted, run.Status);
        Assert.IsTrue(run.CanTick);

        // Opening it again doesn't make a second run.
        await TodayAsync(w, w.Amy, w.AmyScope);
        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(1, await ctx.ChecklistRuns.CountAsync());
        Assert.AreEqual(w.Company.Id, (await ctx.ChecklistRuns.SingleAsync()).CompanyId);
    }

    [TestMethod]
    public async Task GetMyTodayAsync_NotOnTheRota_CantTick()
    {
        World w = await SeedAsync();

        ChecklistRunView run = await TodayAsync(w, w.Tom, SchedulingTestHelpers.StaffScopeFor(w.Ipswich));

        Assert.IsFalse(run.CanTick);
        StringAssert.Contains(run.CantTickReason, "rota");
    }

    [TestMethod]
    public async Task GetMyTodayAsync_ClockedInSomewhereElse_ShowsThatLocation()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = w.Amy.Id, LocationId = w.Wisbech.Id });
            ctx.TimeEntries.Add(new TimeEntry { Id = Guid.NewGuid(), UserId = w.Amy.Id, LocationId = w.Wisbech.Id, ClockInUtc = Now.AddHours(-1), IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Result<ChecklistDayView> day = await GetService(w).GetMyTodayAsync(w.Amy.Id, w.AmyScope);

        Assert.IsTrue(day.IsSuccess, day.Error);
        Assert.AreEqual(w.Wisbech.Id, day.Data!.LocationId);
        Assert.AreEqual(0, day.Data.Runs.Count);
    }

    [TestMethod]
    public async Task GetMyTodayAsync_NotAMemberOfTheLocation_Refused()
    {
        World w = await SeedAsync();

        Result<ChecklistDayView> day = await GetService(w).GetMyTodayAsync(w.Amy.Id, SchedulingTestHelpers.StaffScopeFor(w.Wisbech));

        Assert.IsFalse(day.IsSuccess);
    }

    [TestMethod]
    public async Task GetMyTodayAsync_FeatureOff_Refused()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Checklists, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Result<ChecklistDayView> day = await GetService(w).GetMyTodayAsync(w.Amy.Id, w.AmyScope);

        Assert.IsFalse(day.IsSuccess);
        StringAssert.Contains(day.Error, "switched on");
    }

    [TestMethod]
    public async Task CompleteAsync_RecordsWhoAndWhen_AndRefusesASecondTick()
    {
        World w = await SeedAsync();
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        ChecklistRunService service = GetService(w);

        Result<bool> first = await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), "ignored", 5m);
        Result<bool> second = await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null);

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "Amy");

        await using ApplicationDbContext ctx = new(w.Options);
        ChecklistCompletion tick = await ctx.ChecklistCompletions.SingleAsync();
        Assert.AreEqual(w.Amy.Id, tick.UserId);
        Assert.AreEqual(Now, tick.CompletedUtc);
        Assert.AreEqual(ChecklistCompletionMethod.Phone, tick.Method);
        Assert.IsNull(tick.Note, "A plain tick keeps no note.");
        Assert.IsNull(tick.Reading);
    }

    [TestMethod]
    public async Task CompleteAsync_NumberNeedsAReading_NoteKeptOnNoteTasks()
    {
        World w = await SeedAsync();
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        ChecklistRunService service = GetService(w);

        Assert.IsFalse((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Fridge"), null, null)).IsSuccess);
        Assert.IsFalse((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Fridge"), null, 3.456m)).IsSuccess);
        Assert.IsTrue((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Fridge"), null, 3.5m)).IsSuccess);
        Assert.IsTrue((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Walk round"), "  Wet floor by the slide ", null)).IsSuccess);

        ChecklistRunView after = await TodayAsync(w, w.Amy, w.AmyScope);
        Assert.AreEqual(3.5m, after.Items.Single(x => x.Title == "Fridge").Tick!.Reading);
        Assert.AreEqual("Wet floor by the slide", after.Items.Single(x => x.Title == "Walk round").Tick!.Note);
        Assert.AreEqual(ChecklistRunStatus.InProgress, after.Status);
        Assert.AreEqual(2, after.Done);
    }

    [TestMethod]
    public async Task CompleteAsync_PositionOnlyChecklist_RefusesOthers_AllowsHoldersAndManagers()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.Checklists.SingleAsync()).StaffPositionId = w.DutyManager.Id;
            await ctx.SaveChangesAsync();
        }

        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        ChecklistRunService service = GetService(w);

        Result<bool> amy = await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null);
        Assert.IsFalse(amy.IsSuccess);
        StringAssert.Contains(amy.Error, "Duty Manager");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = w.Amy.Id, StaffPositionId = w.DutyManager.Id, IsPrimary = true });
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null)).IsSuccess);

        // A Manager of the location may tick it without holding the position or being on the rota.
        Assert.IsTrue((await service.CompleteAsync(w.Tom.Id, w.ManagerScope, Item(run, "Walk round"), null, null)).IsSuccess);
    }

    [TestMethod]
    public async Task CompleteAsync_YesterdaysRun_Refused()
    {
        World w = await SeedAsync();
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);

        w.Clock.Advance(TimeSpan.FromDays(1));

        Result<bool> result = await GetService(w).CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "today");
    }

    [TestMethod]
    public async Task CompleteAsync_AnotherLocationsTask_RefusedForANonMember()
    {
        World w = await SeedAsync();
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        UserProfile outsider = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Wisbech, "Olly");

        Result<bool> result = await GetService(w).CompleteAsync(outsider.Id, SchedulingTestHelpers.StaffScopeFor(w.Wisbech), Item(run, "Unlock"), null, null);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.ChecklistCompletions.CountAsync());
    }

    [TestMethod]
    public async Task UndoAsync_OwnTickOrAManager_NotSomeoneElse()
    {
        World w = await SeedAsync();
        ChecklistRunService service = GetService(w);
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null);

        Guid tickId = (await TodayAsync(w, w.Amy, w.AmyScope)).Items.Single(x => x.Title == "Unlock").Tick!.CompletionId;

        Assert.IsFalse((await service.UndoAsync(w.Tom.Id, SchedulingTestHelpers.StaffScopeFor(w.Ipswich), tickId)).IsSuccess);
        Assert.IsTrue((await service.UndoAsync(w.Amy.Id, w.AmyScope, tickId)).IsSuccess);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistCompletion undone = await ctx.ChecklistCompletions.SingleAsync();
            Assert.IsFalse(undone.IsActive);
            Assert.AreEqual(w.Amy.Id, undone.UndoneByUserId);
        }

        // It can be ticked again, and a manager can take anyone's back.
        Assert.IsTrue((await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null)).IsSuccess);
        Guid again = (await TodayAsync(w, w.Amy, w.AmyScope)).Items.Single(x => x.Title == "Unlock").Tick!.CompletionId;
        Assert.IsTrue((await service.UndoAsync(w.Tom.Id, w.ManagerScope, again)).IsSuccess);
    }

    [TestMethod]
    public async Task EditingTasks_ChangesAnUntouchedTodaysRun_ButNotOneWithTicks_OrThePast()
    {
        World w = await SeedAsync();
        ChecklistRunService service = GetService(w);

        // Yesterday's run, made while the tasks were the original three.
        Result<ChecklistDayView> yesterday = await service.GetDayBoardAsync(w.ManagerScope, "manager", w.Ipswich.Id, Today.AddDays(-1));
        Assert.AreEqual(3, yesterday.Data!.Runs.Single().Total);

        await TodayAsync(w, w.Amy, w.AmyScope);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistTask unlock = await ctx.ChecklistTasks.SingleAsync(x => x.Title == "Unlock");
            unlock.Title = "Unlock the fire exits";
            await ctx.SaveChangesAsync();
        }

        // Nobody has ticked today's yet, so it follows the edit.
        ChecklistRunView today = await TodayAsync(w, w.Amy, w.AmyScope);
        Assert.AreEqual("Unlock the fire exits", today.Items[0].Title);

        await service.CompleteAsync(w.Amy.Id, w.AmyScope, today.Items[0].ItemId, null, null);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistTask unlock = await ctx.ChecklistTasks.SingleAsync(x => x.Title == "Unlock the fire exits");
            unlock.Title = "Renamed again";
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual("Unlock the fire exits", (await TodayAsync(w, w.Amy, w.AmyScope)).Items[0].Title, "A ticked run keeps its tasks.");

        Result<ChecklistDayView> pastAgain = await service.GetDayBoardAsync(w.ManagerScope, "manager", w.Ipswich.Id, Today.AddDays(-1));
        Assert.AreEqual("Unlock", pastAgain.Data!.Runs.Single().Items[0].Title, "Past days keep their tasks.");
    }

    [TestMethod]
    public async Task GetHistoryAsync_UntouchedDaysAreMissed_TodayNotYet()
    {
        World w = await SeedAsync();
        ChecklistRunService service = GetService(w);

        Result<List<ChecklistHistoryRow>> history = await service.GetHistoryAsync(w.ManagerScope, w.Ipswich.Id, Today.AddDays(-2), Today.AddDays(5));

        Assert.IsTrue(history.IsSuccess, history.Error);
        Assert.AreEqual(3, history.Data!.Count, "Two past days and today; nothing after today.");
        Assert.AreEqual(Today, history.Data[0].VenueDate);
        Assert.AreEqual(ChecklistRunStatus.NotStarted, history.Data[0].Status);
        Assert.IsTrue(history.Data.Skip(1).All(x => x.Status == ChecklistRunStatus.Missed));
    }

    [TestMethod]
    public async Task GetHistoryAsync_AndBoard_RefuseAnotherLocationsManager_AndLongRanges()
    {
        World w = await SeedAsync();
        ChecklistRunService service = GetService(w);
        LocationScope wisbechManager = SchedulingTestHelpers.ManagerScopeFor(w.Wisbech);

        Assert.IsFalse((await service.GetHistoryAsync(wisbechManager, w.Ipswich.Id, Today.AddDays(-2), Today)).IsSuccess);
        Assert.IsFalse((await service.GetDayBoardAsync(wisbechManager, "manager", w.Ipswich.Id, Today)).IsSuccess);
        Assert.IsFalse((await service.GetHistoryAsync(w.ManagerScope, w.Ipswich.Id, Today.AddDays(-ChecklistRunService.MaxHistoryDays), Today)).IsSuccess);
    }

    [TestMethod]
    public async Task ArchivedChecklist_HiddenFromStaff_KeptOnTheBoardWhenTicked()
    {
        World w = await SeedAsync();
        ChecklistRunService service = GetService(w);
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);
        await service.CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.Checklists.SingleAsync()).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(0, (await service.GetMyTodayAsync(w.Amy.Id, w.AmyScope)).Data!.Runs.Count);
        Assert.AreEqual(1, (await service.GetDayBoardAsync(w.ManagerScope, "manager", w.Ipswich.Id, Today)).Data!.Runs.Count);
    }

    [TestMethod]
    public async Task CompleteAsync_FromChecklistsToday_RecordedAsAManagerTick_OnlyForManagers()
    {
        World w = await SeedAsync();
        ChecklistRunView run = await TodayAsync(w, w.Amy, w.AmyScope);

        Result<bool> staff = await GetService(w).CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Unlock"), null, null, ChecklistCompletionMethod.Manager);
        Assert.IsFalse(staff.IsSuccess, "Staff can't make a manager's tick.");

        Result<bool> manager = await GetService(w).CompleteAsync(w.Tom.Id, w.ManagerScope, Item(run, "Unlock"), null, null, ChecklistCompletionMethod.Manager);
        Assert.IsTrue(manager.IsSuccess, manager.Error);

        Result<bool> terminal = await GetService(w).CompleteAsync(w.Amy.Id, w.AmyScope, Item(run, "Walk round"), null, null, ChecklistCompletionMethod.Terminal);
        Assert.IsFalse(terminal.IsSuccess, "The terminal has its own path.");

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(ChecklistCompletionMethod.Manager, (await ctx.ChecklistCompletions.SingleAsync()).Method);
    }

    [TestMethod]
    public void TickConflict_NamesRacesOnly_AndLeavesOtherFailuresToTheLoggedCatchAll()
    {
        static DbUpdateException Failure(string sqlState) =>
            new("save failed", new Npgsql.PostgresException("boom", "ERROR", "ERROR", sqlState));

        StringAssert.Contains(ChecklistTickConflicts.For(Failure(Npgsql.PostgresErrorCodes.UniqueViolation)), "Someone else ticked");
        StringAssert.Contains(ChecklistTickConflicts.For(Failure(Npgsql.PostgresErrorCodes.ForeignKeyViolation)), "changed a moment ago");
        Assert.IsNull(ChecklistTickConflicts.For(Failure(Npgsql.PostgresErrorCodes.NotNullViolation)));
        Assert.IsNull(ChecklistTickConflicts.For(new DbUpdateException("no inner")));
    }

    [TestMethod]
    public void ATicksTask_CantBeDeletedFromUnderIt()
    {
        // Rebuilding today's untouched run deletes its items; a tick that lands at the same moment
        // must make that fail (Restrict) rather than go with the item (Cascade).
        using ApplicationDbContext ctx = new(SchedulingTestHelpers.GetInMemoryOptions());

        Microsoft.EntityFrameworkCore.Metadata.IForeignKey fk = ctx.Model.FindEntityType(typeof(ChecklistCompletion))!
            .GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(ChecklistRunItem));

        Assert.AreEqual(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }
}
