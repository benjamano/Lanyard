using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.OperationsAlerts;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Checklists;

// Ticking on the clock-in terminal, through the real terminal, PIN and token services: the terminal
// is anonymous, so everything rests on the terminal row and the PIN-started grant.
[TestClass]
public class ChecklistTerminalTests
{
    private const string Pin = "4826";

    // Saturday 21 Nov 2026, 10:00 GMT.
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        TestClock Clock,
        ChecklistRunService Service,
        TimeEntryService TimeEntries,
        Location Ipswich,
        Location Wisbech,
        ClockInTerminal IpswichTerminal,
        ClockInTerminal WisbechTerminal,
        UserProfile Amy,
        UserProfile Manager);

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
        UserProfile manager = await OperationsAlertTestSupport.SeedPersonAsync(options, ipswich, "Mo", "Manager");
        ClockInTerminal ipswichTerminal = await SchedulingTestHelpers.SeedTerminalAsync(options, ipswich, "Front desk");
        ClockInTerminal wisbechTerminal = await SchedulingTestHelpers.SeedTerminalAsync(options, wisbech, "Wisbech desk");

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (Location location in new[] { ipswich, wisbech })
            {
                ctx.Checklists.Add(new Checklist
                {
                    Id = Guid.NewGuid(),
                    CompanyId = company.Id,
                    LocationId = location.Id,
                    Name = $"{location.Name} opening",
                    CreateDate = Now.AddDays(-5),
                    Tasks =
                    [
                        new ChecklistTask { ChecklistId = Guid.Empty, Title = "Unlock", SortOrder = 0 },
                        new ChecklistTask { ChecklistId = Guid.Empty, Title = "Lights", SortOrder = 1 }
                    ]
                });
            }

            ctx.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                LocationId = ipswich.Id,
                UserId = amy.Id,
                StartUtc = Now.AddHours(-1),
                EndUtc = Now.AddHours(6),
                IsActive = true,
                PublishedDateUtc = Now.AddDays(-2),
                CreateByUserId = "manager"
            });

            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(Now);
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        ClockInPinService pins = new(factory, new PasswordHasher<UserProfile>());
        await pins.SetOwnPinAsync(amy.Id, Pin);
        await pins.SetOwnPinAsync(manager.Id, Pin);

        TerminalEphemeralTokenService tokens = new(clock);
        ClockInTerminalService terminals = new(factory, clock, NullLogger<ClockInTerminalService>.Instance);
        TimeEntryService timeEntries = new(factory, pins, terminals, new SchedulingSettingsService(factory), tokens, new TerminalEventBus(), clock,
            NullLogger<TimeEntryService>.Instance);

        ChecklistRunService service = new(factory, terminals, timeEntries, tokens, new Lanyard.Application.Services.VideoStreaming.VideoStreamTokenService(), clock, NullLogger<ChecklistRunService>.Instance);

        return new World(options, clock, service, timeEntries, ipswich, wisbech, ipswichTerminal, wisbechTerminal, amy, manager);
    }

    private static async Task<ChecklistRunView> DayAsync(World w, ClockInTerminal terminal, string? grant)
    {
        Result<ChecklistDayView> day = await w.Service.GetTerminalDayAsync(terminal.Id, grant);
        Assert.IsTrue(day.IsSuccess, day.Error);

        return day.Data!.Runs.Single();
    }

    private static async Task<string> StartAsync(World w, ClockInTerminal terminal, UserProfile user)
    {
        Result<string> grant = await w.Service.StartTerminalTickingAsync(terminal.Id, user.Id, Pin);
        Assert.IsTrue(grant.IsSuccess, grant.Error);

        return grant.Data!;
    }

    [TestMethod]
    public async Task Pin_GivesAGrant_TickIsRecordedAsTheTerminal_AndNobodyIsClockedIn()
    {
        World w = await SeedAsync();

        ChecklistRunView before = await DayAsync(w, w.IpswichTerminal, null);
        Assert.IsFalse(before.CanTick, "Without a PIN nobody can tick.");
        Assert.AreEqual("Ipswich opening", before.Name, "The terminal only shows its own location.");

        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView run = await DayAsync(w, w.IpswichTerminal, grant);
        Assert.IsTrue(run.CanTick);

        Result<bool> ticked = await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, run.Items[0].ItemId, null, null);
        Assert.IsTrue(ticked.IsSuccess, ticked.Error);

        await using ApplicationDbContext ctx = new(w.Options);
        ChecklistCompletion tick = await ctx.ChecklistCompletions.SingleAsync();
        Assert.AreEqual(w.Amy.Id, tick.UserId);
        Assert.AreEqual(ChecklistCompletionMethod.Terminal, tick.Method);
        Assert.AreEqual(w.IpswichTerminal.Id, tick.ClockInTerminalId);
        Assert.AreEqual(0, await ctx.TimeEntries.CountAsync(), "Checking a PIN for checklists mustn't clock anyone in.");
    }

    [TestMethod]
    public async Task GrantFromOneTerminal_IsRefusedOnAnother()
    {
        World w = await SeedAsync();
        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView wisbech = await DayAsync(w, w.WisbechTerminal, grant);

        Assert.IsFalse(wisbech.CanTick);

        Result<bool> result = await w.Service.CompleteOnTerminalAsync(w.WisbechTerminal.Id, grant, wisbech.Items[0].ItemId, null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "PIN again");
    }

    [TestMethod]
    public async Task AnotherLocationsItem_WithAValidGrant_IsNotFound()
    {
        World w = await SeedAsync();
        ChecklistRunView wisbech = await DayAsync(w, w.WisbechTerminal, null);
        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);

        Result<bool> result = await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, wisbech.Items[0].ItemId, null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "wasn't found");

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.ChecklistCompletions.CountAsync());
    }

    [TestMethod]
    public async Task Grant_LapsesTwoMinutesAfterItsLastUse_EachUseSlidesItOn()
    {
        World w = await SeedAsync();
        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView run = await DayAsync(w, w.IpswichTerminal, grant);

        w.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.IsTrue((await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, run.Items[0].ItemId, null, null)).IsSuccess);

        // 90 seconds after the last use: still inside the slid-on two minutes.
        w.Clock.Advance(TimeSpan.FromSeconds(90));
        ChecklistRunView later = await DayAsync(w, w.IpswichTerminal, grant);
        Assert.IsTrue(later.CanTick);

        w.Clock.Advance(TerminalEphemeralTokenService.ChecklistGrantIdle + TimeSpan.FromSeconds(1));
        Result<bool> lapsed = await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, run.Items[1].ItemId, null, null);

        Assert.IsFalse(lapsed.IsSuccess);
        StringAssert.Contains(lapsed.Error, "PIN again");
    }

    [TestMethod]
    public async Task EndTerminalTicking_RevokesTheGrant()
    {
        World w = await SeedAsync();
        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView run = await DayAsync(w, w.IpswichTerminal, grant);

        w.Service.EndTerminalTicking(grant);

        Assert.IsFalse((await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, run.Items[0].ItemId, null, null)).IsSuccess);
    }

    [TestMethod]
    public async Task UnpairedTerminal_IsRefused_EvenWithAGrant()
    {
        World w = await SeedAsync();
        string grant = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView run = await DayAsync(w, w.IpswichTerminal, grant);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.ClockInTerminals.SingleAsync(x => x.Id == w.IpswichTerminal.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Assert.IsFalse((await w.Service.GetTerminalDayAsync(w.IpswichTerminal.Id, grant)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, grant, run.Items[0].ItemId, null, null)).IsSuccess);
    }

    [TestMethod]
    public async Task WrongPins_ShareTheClockInLockout()
    {
        World w = await SeedAsync();

        for (int i = 0; i < TerminalEphemeralTokenService.MaxPinFailures; i++)
        {
            Assert.IsFalse((await w.Service.StartTerminalTickingAsync(w.IpswichTerminal.Id, w.Amy.Id, "0000")).IsSuccess);
        }

        // Now locked out of clocking in too, even with the right PIN.
        Result<ClockActionResult> clock = await w.TimeEntries.ClockByPinAsync(w.IpswichTerminal.Id, w.Amy.Id, Pin);
        Result<string> checklists = await w.Service.StartTerminalTickingAsync(w.IpswichTerminal.Id, w.Amy.Id, Pin);

        Assert.IsFalse(clock.IsSuccess);
        Assert.IsFalse(checklists.IsSuccess);
        StringAssert.Contains(checklists.Error, "wrong");
    }

    [TestMethod]
    public async Task SomeoneFromAnotherLocation_CantStartTicking()
    {
        World w = await SeedAsync();

        Result<string> grant = await w.Service.StartTerminalTickingAsync(w.WisbechTerminal.Id, w.Amy.Id, Pin);

        Assert.IsFalse(grant.IsSuccess);
        StringAssert.Contains(grant.Error, "don't work");
    }

    [TestMethod]
    public async Task People_AreTodaysRosterPlusTheLocationsManagers()
    {
        World w = await SeedAsync();

        Result<List<TerminalChecklistPerson>> people = await w.Service.GetTerminalPeopleAsync(w.IpswichTerminal.Id);

        Assert.IsTrue(people.IsSuccess, people.Error);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id, w.Manager.Id }, people.Data!.Select(x => x.UserId).ToArray());
        Assert.IsTrue(people.Data.All(x => x.HasPin));
    }

    [TestMethod]
    public async Task People_IncludeSomeoneWhoseShiftFinishedEarlierToday()
    {
        World w = await SeedAsync();
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Tom");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                LocationId = w.Ipswich.Id,
                UserId = tom.Id,
                StartUtc = Now.AddHours(-9),
                EndUtc = Now.AddHours(-5),
                IsActive = true,
                PublishedDateUtc = Now.AddDays(-2),
                CreateByUserId = "manager"
            });
            await ctx.SaveChangesAsync();
        }

        Result<List<TerminalChecklistPerson>> people = await w.Service.GetTerminalPeopleAsync(w.IpswichTerminal.Id);

        TerminalChecklistPerson listed = people.Data!.Single(x => x.UserId == tom.Id);
        Assert.IsFalse(listed.HasPin, "Tom has no PIN, so the terminal tells him to use his phone.");
    }

    [TestMethod]
    public async Task Manager_NotOnTheRota_CanStillTick_AndTakeBackSomeoneElsesTick()
    {
        World w = await SeedAsync();
        string amy = await StartAsync(w, w.IpswichTerminal, w.Amy);
        ChecklistRunView run = await DayAsync(w, w.IpswichTerminal, amy);
        await w.Service.CompleteOnTerminalAsync(w.IpswichTerminal.Id, amy, run.Items[0].ItemId, null, null);
        w.Service.EndTerminalTicking(amy);

        string manager = await StartAsync(w, w.IpswichTerminal, w.Manager);
        ChecklistRunView managerView = await DayAsync(w, w.IpswichTerminal, manager);

        Assert.IsTrue(managerView.CanTick);
        Assert.IsTrue((await w.Service.UndoOnTerminalAsync(w.IpswichTerminal.Id, manager, managerView.Items[0].Tick!.CompletionId)).IsSuccess);
    }
}
