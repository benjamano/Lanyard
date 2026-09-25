using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class TimeEntryServiceTests
{
    // Monday 5 October 2026 is British Summer Time: 09:00 local is 08:00 UTC.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private const string Pin = "1234";

    private static DateTime Local(DateOnly day, int hour, int minute = 0) => RotaTime.ToUtc(day, new TimeOnly(hour, minute));

    private sealed class Fixture
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required TestClock Clock { get; init; }
        public required TimeEntryService Service { get; init; }
        public required TerminalEphemeralTokenService Tokens { get; init; }
        public required ClockInPinService Pins { get; init; }
        public required List<TerminalClockEvent> Events { get; init; }
        public required Company Company { get; init; }
        public required Location Location { get; init; }
        public required ClockInTerminal Terminal { get; init; }
        public required UserProfile Ben { get; init; }
    }

    private static async Task<Fixture> CreateAsync(int localHour = 8, int localMinute = 30)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInTerminal terminal = await SchedulingTestHelpers.SeedTerminalAsync(options, location);

        TestClock clock = new(Local(Monday, localHour, localMinute));
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        ClockInPinService pins = new(factory, new PasswordHasher<UserProfile>());
        await pins.SetOwnPinAsync(ben.Id, Pin);

        TerminalEphemeralTokenService tokens = new(clock);
        TerminalEventBus bus = new();
        List<TerminalClockEvent> events = [];
        bus.OnClock += events.Add;

        TimeEntryService service = new(
            factory,
            pins,
            new ClockInTerminalService(factory, clock, NullLogger<ClockInTerminalService>.Instance),
            new SchedulingSettingsService(factory),
            tokens,
            bus,
            clock,
            NullLogger<TimeEntryService>.Instance);

        return new Fixture
        {
            Options = options, Clock = clock, Service = service, Tokens = tokens, Pins = pins, Events = events,
            Company = company, Location = location, Terminal = terminal, Ben = ben
        };
    }

    private static async Task<Shift> SeedShiftAsync(Fixture f, UserProfile user, DateOnly day, int startHour, int endHour, bool published = true, Location? location = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = (location ?? f.Location).Id,
            UserId = user.Id,
            StartUtc = Local(day, startHour),
            EndUtc = Local(day, endHour),
            CreateByUserId = "manager",
            PublishedDateUtc = published ? DateTime.UtcNow : null,
            IsActive = true
        };

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task<TimeEntry> SeedOpenEntryAsync(Fixture f, UserProfile user, DateTime clockInUtc, Location? location = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);

        TimeEntry entry = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            LocationId = (location ?? f.Location).Id,
            ClockInUtc = clockInUtc,
            ClockInMethod = ClockMethod.Pin,
            IsActive = true
        };

        ctx.TimeEntries.Add(entry);
        await ctx.SaveChangesAsync();

        return entry;
    }

    // --- Roster ---

    [TestMethod]
    public async Task GetTerminalRosterAsync_ListsTodaysPublishedShiftsAndAnyoneClockedIn()
    {
        Fixture f = await CreateAsync();
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Amy");
        UserProfile drafted = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Dan");
        UserProfile covering = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Cat");
        UserProfile tomorrow = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Tia");
        UserProfile finished = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Fin");
        UserProfile forgot = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Flo");

        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        await SeedShiftAsync(f, amy, Monday, 12, 20);
        await SeedShiftAsync(f, drafted, Monday, 9, 17, published: false);
        await SeedShiftAsync(f, tomorrow, Monday.AddDays(1), 9, 17);
        await SeedShiftAsync(f, finished, Monday, 2, 6);
        await SeedOpenEntryAsync(f, covering, Local(Monday, 8));

        // Forgot to clock out yesterday: their next tap clocks them in, so they aren't on the clock.
        await SeedOpenEntryAsync(f, forgot, Local(Monday.AddDays(-1), 8));

        Result<List<TerminalRosterEntry>> result = await f.Service.GetTerminalRosterAsync(f.Terminal.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        // A shared tablet anyone can see: first name and surname initial only.
        CollectionAssert.AreEqual(new[] { "Ben T.", "Amy T.", "Cat T." }, result.Data!.Select(x => x.DisplayName).ToArray());
        Assert.AreEqual("09:00–17:00", result.Data[0].ShiftSummary);
        Assert.IsTrue(result.Data[0].HasPin);
        Assert.IsFalse(result.Data[1].HasPin);
        Assert.IsTrue(result.Data[2].IsClockedIn);
    }

    // --- Clocking in and out by PIN ---

    [TestMethod]
    public async Task ClockByPinAsync_ClocksInWithinTheWindowAndLinksTheShift()
    {
        Fixture f = await CreateAsync(8, 30);
        Shift shift = await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClockDirection.In, result.Data!.Direction);
        Assert.AreEqual("Ben", result.Data.GreetingName);
        Assert.AreEqual("09:00–17:00", result.Data.ShiftSummary);

        await using ApplicationDbContext ctx = new(f.Options);
        TimeEntry entry = await ctx.TimeEntries.SingleAsync();
        Assert.AreEqual(shift.Id, entry.ShiftId);
        Assert.AreEqual(f.Terminal.Id, entry.ClockInTerminalId);
        Assert.AreEqual(ClockMethod.Pin, entry.ClockInMethod);
        Assert.AreEqual(f.Clock.UtcNow, entry.ClockInUtc);
        Assert.AreEqual(ClockMethod.Pin, f.Events.Single().Method);
    }

    [TestMethod]
    public async Task ClockByPinAsync_RefusesMoreThanTheWindowBeforeAShiftAndNamesTheNextOne()
    {
        Fixture f = await CreateAsync(7, 0);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "not on the rota right now");
        StringAssert.Contains(result.Error, "Mon 5 Oct at 09:00");

        await using ApplicationDbContext ctx = new(f.Options);
        Assert.AreEqual(0, await ctx.TimeEntries.CountAsync());
    }

    [TestMethod]
    public async Task ClockByPinAsync_AllowsClockingInLateUpToTheWindowAfterTheShiftEnds()
    {
        Fixture f = await CreateAsync(17, 45);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task ClockByPinAsync_RespectsTheCompanysClockInWindow()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = f.Company.Id, ClockInWindowMinutes = 15 });
            await ctx.SaveChangesAsync();
        }

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ClockByPinAsync_IgnoresDraftShifts()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17, published: false);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ClockByPinAsync_ClocksOutWhenAlreadyIn_EvenOutsideTheWindow()
    {
        Fixture f = await CreateAsync(19, 0);
        Shift shift = await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        TimeEntry open = await SeedOpenEntryAsync(f, f.Ben, Local(Monday, 9));

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            (await ctx.TimeEntries.SingleAsync()).ShiftId = shift.Id;
            await ctx.SaveChangesAsync();
        }

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClockDirection.Out, result.Data!.Direction);
        Assert.AreEqual(10m, result.Data.ClockedHours);

        await using ApplicationDbContext verify = new(f.Options);
        TimeEntry closed = await verify.TimeEntries.SingleAsync(x => x.Id == open.Id);
        Assert.AreEqual(f.Clock.UtcNow, closed.ClockOutUtc);
        Assert.IsFalse(closed.NeedsReview);
    }

    [TestMethod]
    public async Task ClockByPinAsync_ClockingOutAtAnotherLocationFlagsTheEntry()
    {
        Fixture f = await CreateAsync(17, 0);
        Location elsewhere;

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            elsewhere = new Location { CompanyId = f.Company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(elsewhere);
            await ctx.SaveChangesAsync();
        }

        await SeedOpenEntryAsync(f, f.Ben, Local(Monday, 9), elsewhere);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verify = new(f.Options);
        TimeEntry entry = await verify.TimeEntries.SingleAsync();
        Assert.IsTrue(entry.NeedsReview);
        StringAssert.Contains(entry.ReviewReason, "Wisbech");
    }

    [TestMethod]
    public async Task ClockByPinAsync_ClosesAnEntryLeftOpenOvernightThenClocksIn()
    {
        Fixture f = await CreateAsync(8, 45);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        TimeEntry stale = await SeedOpenEntryAsync(f, f.Ben, Local(Monday.AddDays(-1), 9));

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClockDirection.In, result.Data!.Direction);

        await using ApplicationDbContext verify = new(f.Options);
        TimeEntry closed = await verify.TimeEntries.SingleAsync(x => x.Id == stale.Id);
        Assert.AreEqual(stale.ClockInUtc.AddHours(16), closed.ClockOutUtc);
        Assert.AreEqual(ClockMethod.Automatic, closed.ClockOutMethod);
        Assert.IsTrue(closed.NeedsReview);
        Assert.AreEqual(1, await verify.TimeEntries.CountAsync(x => x.ClockOutUtc == null));
    }

    [TestMethod]
    public async Task ClockByPinAsync_WrongPinIsRejectedAndFiveInARowLocksThePerson()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        for (int i = 0; i < 4; i++)
        {
            Result<ClockActionResult> wrong = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, "9999");
            StringAssert.Contains(wrong.Error, "PIN isn't right");
        }

        Result<ClockActionResult> fifth = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, "9999");
        Result<ClockActionResult> rightButLocked = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        StringAssert.Contains(fifth.Error, "Too many wrong PINs");
        Assert.IsFalse(rightButLocked.IsSuccess);

        f.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsTrue((await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin)).IsSuccess);
    }

    [TestMethod]
    public async Task ClockByPinAsync_PersonWithNoPinIsToldToScanInstead()
    {
        Fixture f = await CreateAsync(8, 30);
        await f.Pins.ClearOwnPinAsync(f.Ben.Id);

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        StringAssert.Contains(result.Error, "haven't set a clock-in PIN");
    }

    [TestMethod]
    public async Task ClockByPinAsync_RejectsARevokedTerminal()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            (await ctx.ClockInTerminals.SingleAsync()).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        StringAssert.Contains(result.Error, "unpaired");
    }

    [TestMethod]
    public async Task ClockByPinAsync_RejectsSomeoneWhoDoesNotWorkAtTheLocation()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships);
            await ctx.SaveChangesAsync();
        }

        Result<ClockActionResult> result = await f.Service.ClockByPinAsync(f.Terminal.Id, f.Ben.Id, Pin);

        StringAssert.Contains(result.Error, "not set up to work");
    }

    // --- Clocking by QR ---

    [TestMethod]
    public async Task ClockByQrAsync_UsesTheTerminalsLocationAndGreetsOnTheTerminal()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        string nonce = f.Tokens.IssueQrNonce(f.Terminal.Id);

        Result<ClockActionResult> result = await f.Service.ClockByQrAsync(nonce, f.Ben.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        TerminalClockEvent clockEvent = f.Events.Single();
        Assert.AreEqual(f.Terminal.Id, clockEvent.TerminalId);
        Assert.AreEqual(ClockMethod.Qr, clockEvent.Method);

        await using ApplicationDbContext ctx = new(f.Options);
        TimeEntry entry = await ctx.TimeEntries.SingleAsync();
        Assert.AreEqual(f.Location.Id, entry.LocationId);
        Assert.AreEqual(ClockMethod.Qr, entry.ClockInMethod);
    }

    [TestMethod]
    public async Task ClockByQrAsync_CodeCanOnlyBeUsedOnce()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        string nonce = f.Tokens.IssueQrNonce(f.Terminal.Id);

        await f.Service.ClockByQrAsync(nonce, f.Ben.Id);
        Result<ClockActionResult> second = await f.Service.ClockByQrAsync(nonce, f.Ben.Id);

        StringAssert.Contains(second.Error, "expired");
    }

    [TestMethod]
    public async Task ClockByQrAsync_RejectsAnExpiredCode()
    {
        Fixture f = await CreateAsync(8, 30);
        await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        string nonce = f.Tokens.IssueQrNonce(f.Terminal.Id);

        f.Clock.Advance(TimeSpan.FromSeconds(61));

        Result<ClockActionResult> result = await f.Service.ClockByQrAsync(nonce, f.Ben.Id);

        StringAssert.Contains(result.Error, "expired");
    }

    // --- Timesheets ---

    [TestMethod]
    public async Task GetTimesheetAsync_PairsEntriesWithShiftsAndShowsMissedShifts()
    {
        Fixture f = await CreateAsync(20, 0);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Amy");
        Shift worked = await SeedShiftAsync(f, f.Ben, Monday, 9, 17);
        await SeedShiftAsync(f, amy, Monday, 10, 14);

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.TimeEntries.Add(new TimeEntry
            {
                Id = Guid.NewGuid(), UserId = f.Ben.Id, LocationId = f.Location.Id, ShiftId = worked.Id,
                ClockInUtc = Local(Monday, 9, 5), ClockOutUtc = Local(Monday, 17, 5), IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        Result<TimesheetView> result = await f.Service.GetTimesheetAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), f.Location.Id, Monday, Monday.AddDays(6));

        Assert.IsTrue(result.IsSuccess, result.Error);

        TimesheetPerson ben = result.Data!.People.Single(x => x.UserId == f.Ben.Id);
        Assert.AreEqual(8m, ben.ClockedHours);
        Assert.AreEqual(8m, ben.ScheduledHours);
        Assert.AreEqual(1, ben.AwaitingApprovalCount);
        Assert.AreEqual(worked.Id, ben.Lines.Single().Shift!.Id);

        TimesheetLine amyLine = result.Data.People.Single(x => x.UserId == amy.Id).Lines.Single();
        Assert.IsNull(amyLine.Entry);
        Assert.IsTrue(amyLine.IsMissed(f.Clock.UtcNow));
    }

    [TestMethod]
    public async Task GetTimesheetAsync_RejectsStaffWithoutManagerRole()
    {
        Fixture f = await CreateAsync();

        Result<TimesheetView> result = await f.Service.GetTimesheetAsync(SchedulingTestHelpers.StaffScopeFor(f.Location), f.Location.Id, Monday, Monday.AddDays(6));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveEntryAsync_ManualEntryIsApprovedByTheManagerAndLinkedToTheShift()
    {
        Fixture f = await CreateAsync(20, 0);
        Shift shift = await SeedShiftAsync(f, f.Ben, Monday, 9, 17);

        Result<TimeEntry> result = await f.Service.SaveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), new TimeEntry
        {
            UserId = f.Ben.Id,
            LocationId = f.Location.Id,
            ClockInUtc = Local(Monday, 9),
            ClockOutUtc = Local(Monday, 17),
            Notes = "Forgot to clock in"
        }, "manager");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClockMethod.Manual, result.Data!.ClockInMethod);
        Assert.AreEqual(shift.Id, result.Data.ShiftId);
        Assert.AreEqual("manager", result.Data.ApprovedByUserId);
    }

    [TestMethod]
    public async Task SaveEntryAsync_CorrectingAFlaggedEntryClearsTheFlag()
    {
        Fixture f = await CreateAsync(20, 0);
        TimeEntry flagged;

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            flagged = new TimeEntry
            {
                Id = Guid.NewGuid(), UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 9),
                ClockOutUtc = Local(Monday, 9).AddHours(16), ClockOutMethod = ClockMethod.Automatic, NeedsReview = true, ReviewReason = "Closed automatically", IsActive = true
            };
            ctx.TimeEntries.Add(flagged);
            await ctx.SaveChangesAsync();
        }

        Result<TimeEntry> result = await f.Service.SaveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), new TimeEntry
        {
            Id = flagged.Id, UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 9), ClockOutUtc = Local(Monday, 17)
        }, "manager");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.NeedsReview);
        Assert.IsNull(result.Data.ReviewReason);
        Assert.AreEqual(ClockMethod.Manual, result.Data.ClockOutMethod);
        Assert.AreEqual(8m, result.Data.ClockedHours);
    }

    [TestMethod]
    public async Task SaveEntryAsync_RejectsOverlappingTime()
    {
        Fixture f = await CreateAsync(20, 0);
        await SeedOpenEntryAsync(f, f.Ben, Local(Monday, 9));

        Result<TimeEntry> result = await f.Service.SaveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), new TimeEntry
        {
            UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 12), ClockOutUtc = Local(Monday, 14)
        }, "manager");

        StringAssert.Contains(result.Error, "overlaps");
    }

    [TestMethod]
    public async Task SaveEntryAsync_RejectsClockOutBeforeClockIn()
    {
        Fixture f = await CreateAsync(20, 0);

        Result<TimeEntry> result = await f.Service.SaveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), new TimeEntry
        {
            UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 17), ClockOutUtc = Local(Monday, 9)
        }, "manager");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveEntryAsync_RejectsAClockOutInTheFuture()
    {
        Fixture f = await CreateAsync(10, 0);

        // Typed in at 10:00: a closed 09:00-17:00 entry would let a terminal clock-in at 12:00
        // overlap it.
        Result<TimeEntry> result = await f.Service.SaveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), new TimeEntry
        {
            UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 9), ClockOutUtc = Local(Monday, 17)
        }, "manager");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "future");
    }

    [TestMethod]
    public async Task ApproveEntryAsync_RefusesAnEntryStillOnTheClock()
    {
        Fixture f = await CreateAsync(12, 0);
        TimeEntry open = await SeedOpenEntryAsync(f, f.Ben, Local(Monday, 9));

        Result<bool> result = await f.Service.ApproveEntryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), open.Id, "manager");

        StringAssert.Contains(result.Error, "still clocked in");
    }

    [TestMethod]
    public async Task ApproveAllAsync_ApprovesFinishedEntriesButLeavesFlaggedOnesForReview()
    {
        Fixture f = await CreateAsync(20, 0);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Location, "Amy");

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.TimeEntries.AddRange(
                new TimeEntry { Id = Guid.NewGuid(), UserId = f.Ben.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 9), ClockOutUtc = Local(Monday, 17), IsActive = true },
                new TimeEntry { Id = Guid.NewGuid(), UserId = amy.Id, LocationId = f.Location.Id, ClockInUtc = Local(Monday, 9), ClockOutUtc = Local(Monday, 12), NeedsReview = true, IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Result<int> result = await f.Service.ApproveAllAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), f.Location.Id, Monday, Monday.AddDays(6), "manager");

        Assert.AreEqual(1, result.Data);

        await using ApplicationDbContext verify = new(f.Options);
        Assert.IsTrue((await verify.TimeEntries.SingleAsync(x => x.UserId == f.Ben.Id)).IsApproved);
        Assert.IsFalse((await verify.TimeEntries.SingleAsync(x => x.UserId == amy.Id)).IsApproved);
    }
}
