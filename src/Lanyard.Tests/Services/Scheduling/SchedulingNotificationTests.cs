using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Scheduling;

// Who gets told what: rota publishes, time-off requests and decisions, shift reminders, and the
// "Who's on today" board.
[TestClass]
public class SchedulingNotificationTests
{
    // Thursday 24 September 2026, 11:00 UTC (12:00 in Peterborough).
    private static readonly DateTime Now = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 24);
    private const string Manager = "manager-user";

    private static RotaService GetRota(DbContextOptions<ApplicationDbContext> options, RecordingNotificationDispatcher notifications)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new RotaService(factory, new ContractRequirementService(factory), notifications, NullLogger<RotaService>.Instance);
    }

    private static TimeOffService GetTimeOff(DbContextOptions<ApplicationDbContext> options, RecordingNotificationDispatcher notifications)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new TimeOffService(factory, new SchedulingSettingsService(factory), new TimeOffEventBus(), notifications, new TestClock(Now), NullLogger<TimeOffService>.Instance);
    }

    private static async Task<Shift> AddShiftAsync(DbContextOptions<ApplicationDbContext> options, Location location, UserProfile user, DateTime startUtc, int hours = 4,
        DateTime? publishedUtc = null, DateTime? remindedFor = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = user.Id,
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(hours),
            PublishedDateUtc = publishedUtc,
            ReminderSentForStartUtc = remindedFor,
            IsActive = true,
            CreateDate = Now.AddDays(-7),
            CreateByUserId = Manager
        };

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task GiveRoleAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user, string roleName, bool roleActive = true)
    {
        await using ApplicationDbContext ctx = new(options);

        ApplicationRole? role = await ctx.Roles.FirstOrDefaultAsync(x => x.Name == roleName && x.IsActive == roleActive);

        if (role is null)
        {
            role = new ApplicationRole { Id = Guid.NewGuid().ToString(), Name = roleName, NormalizedName = roleName.ToUpperInvariant(), CreatedByUserId = "seed", IsActive = roleActive };
            ctx.Roles.Add(role);
        }

        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        await ctx.SaveChangesAsync();
    }

    private static async Task<TimeOffType> HolidayTypeAsync(DbContextOptions<ApplicationDbContext> options, Company company)
    {
        await using ApplicationDbContext ctx = new(options);
        TimeOffType type = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Paid holiday", IsPaid = true, DeductsFromAllowance = true };
        ctx.TimeOffTypes.Add(type);
        await ctx.SaveChangesAsync();
        return type;
    }

    private static async Task<TimeOffRequest> AddRequestAsync(DbContextOptions<ApplicationDbContext> options, Location location, UserProfile user, TimeOffType type,
        DateOnly start, DateOnly end, TimeOffStatus status)
    {
        await using ApplicationDbContext ctx = new(options);

        TimeOffRequest request = new()
        {
            Id = Guid.NewGuid(), UserId = user.Id, TimeOffTypeId = type.Id, LocationId = location.Id,
            StartDate = start, EndDate = end, Hours = (end.DayNumber - start.DayNumber + 1) * 8, Status = status, RequestedDateUtc = Now.AddDays(-3)
        };

        ctx.TimeOffRequests.Add(request);
        await ctx.SaveChangesAsync();

        return request;
    }

    // --- Rota publish -------------------------------------------------------------------------

    [TestMethod]
    public async Task Publish_EmailsEachAffectedPersonTheirOwnChangesOnly()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, location, "Priya");
        DateOnly monday = new(2026, 10, 5);

        await AddShiftAsync(options, location, amy, RotaTime.ToUtc(monday, new TimeOnly(9, 0)));
        await AddShiftAsync(options, location, amy, RotaTime.ToUtc(monday.AddDays(2), new TimeOnly(12, 0)));
        await AddShiftAsync(options, location, tom, RotaTime.ToUtc(monday.AddDays(1), new TimeOnly(9, 0)));
        await AddShiftAsync(options, location, priya, RotaTime.ToUtc(monday, new TimeOnly(9, 0)), publishedUtc: Now.AddDays(-1));

        RecordingNotificationDispatcher notifications = new();

        Result<PublishResult> result = await GetRota(options, notifications).PublishRangeAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), location.Id, monday, monday.AddDays(6), Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, notifications.Jobs.Count, "Priya's shift was already published and untouched.");

        RotaChangedPayload amyPayload = (RotaChangedPayload)notifications.Jobs.Single(x => x.UserId == amy.Id).Payload;
        Assert.AreEqual(NotificationTopic.RotaChanged, notifications.Jobs[0].Topic);
        Assert.AreEqual("Ipswich", amyPayload.LocationName);
        Assert.AreEqual(2, amyPayload.Added.Count);
        Assert.AreEqual("09:00–13:00", amyPayload.Added[0].TimeRange);
        Assert.AreEqual(monday, amyPayload.Added[0].Date);
    }

    // --- Time off -----------------------------------------------------------------------------

    [TestMethod]
    public async Task Submit_EmailsTheManagersOfThatLocationOnly()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile ada = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ada");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        UserProfile olly = await SchedulingTestHelpers.SeedUserAsync(options, elsewhere, "Olly");
        UserProfile rex = await SchedulingTestHelpers.SeedUserAsync(options, location, "Rex");
        await GiveRoleAsync(options, sam, "Manager");
        await GiveRoleAsync(options, ada, "Admin");
        await GiveRoleAsync(options, olly, "Manager");
        await GiveRoleAsync(options, rex, "Manager", roleActive: false);
        TimeOffType holiday = await HolidayTypeAsync(options, company);

        RecordingNotificationDispatcher notifications = new();

        Result<TimeOffSubmitResult> result = await GetTimeOff(options, notifications).SubmitAsync(amy.Id, location.Id,
            new TimeOffRequestDraft(holiday.Id, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6), 16, "Wedding"));

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(new[] { sam.Id, ada.Id }, notifications.Jobs.Select(x => x.UserId).ToArray(),
            $"Not {tom.FirstName} (no role), Olly (another location) or Rex (archived role).");

        TimeOffRequestedPayload payload = (TimeOffRequestedPayload)notifications.Jobs[0].Payload;
        Assert.AreEqual("Amy Tester", payload.RequesterName);
        Assert.AreEqual("2 days (16 h)", payload.Amount);
        Assert.AreEqual("Wedding", payload.Notes);
    }

    [TestMethod]
    public async Task Submit_ByAManagerDoesNotEmailThemselves()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        await GiveRoleAsync(options, sam, "Manager");
        TimeOffType holiday = await HolidayTypeAsync(options, company);
        RecordingNotificationDispatcher notifications = new();

        await GetTimeOff(options, notifications).SubmitAsync(sam.Id, location.Id,
            new TimeOffRequestDraft(holiday.Id, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5), 8, null));

        Assert.AreEqual(0, notifications.Jobs.Count);
    }

    [TestMethod]
    public async Task Record_TellsThePersonItWasRecorded()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        TimeOffType holiday = await HolidayTypeAsync(options, company);
        RecordingNotificationDispatcher notifications = new();

        await GetTimeOff(options, notifications).RecordForUserAsync(SchedulingTestHelpers.ManagerScopeFor(location), amy.Id, location.Id,
            new TimeOffRequestDraft(holiday.Id, Today, Today, 8, "Called in sick"), Manager);

        NotificationJob job = notifications.Jobs.Single();
        TimeOffDecidedPayload payload = (TimeOffDecidedPayload)job.Payload;
        Assert.AreEqual(amy.Id, job.UserId);
        Assert.AreEqual(TimeOffEmailOutcome.Recorded, payload.Outcome);
        Assert.AreEqual("Called in sick", payload.Reason);
    }

    [TestMethod]
    [DataRow(TimeOffStatus.Pending, true, TimeOffEmailOutcome.Approved)]
    [DataRow(TimeOffStatus.Pending, false, TimeOffEmailOutcome.Rejected)]
    [DataRow(TimeOffStatus.Approved, false, TimeOffEmailOutcome.Withdrawn)]
    public async Task Decide_TellsThePersonWhatHappened(TimeOffStatus before, bool approve, TimeOffEmailOutcome expected)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        TimeOffType holiday = await HolidayTypeAsync(options, company);
        TimeOffRequest request = await AddRequestAsync(options, location, amy, holiday, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6), before);
        RecordingNotificationDispatcher notifications = new();

        Result<TimeOffRequest> result = await GetTimeOff(options, notifications).DecideAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), request.Id, approve, approve ? null : "Short-staffed", Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        TimeOffDecidedPayload payload = (TimeOffDecidedPayload)notifications.Jobs.Single(x => x.UserId == amy.Id).Payload;
        Assert.AreEqual(expected, payload.Outcome);
        Assert.AreEqual("Paid holiday", payload.TypeName);
    }

    [TestMethod]
    public async Task Decide_CuttingShortNamesTheDaysThatWereWithdrawn()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        TimeOffType holiday = await HolidayTypeAsync(options, company);
        TimeOffRequest request = await AddRequestAsync(options, location, amy, holiday, Today.AddDays(-1), Today.AddDays(3), TimeOffStatus.Approved);
        RecordingNotificationDispatcher notifications = new();

        await GetTimeOff(options, notifications).DecideAsync(SchedulingTestHelpers.ManagerScopeFor(location), request.Id, false, "Needed back", Manager);

        TimeOffDecidedPayload payload = (TimeOffDecidedPayload)notifications.Jobs.Single().Payload;
        Assert.AreEqual(TimeOffEmailOutcome.CutShort, payload.Outcome);
        Assert.AreEqual(Today.AddDays(1), payload.Start);
        Assert.AreEqual(Today.AddDays(3), payload.End);
    }

    // --- Shift reminders ----------------------------------------------------------------------

    [TestMethod]
    public async Task Reminders_DueOnlyInsideTheLeadTimeForPublishedUnremindedShifts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        DateTime longAgo = Now.AddDays(-5);

        Shift due = await AddShiftAsync(options, location, amy, Now.AddHours(20), publishedUtc: longAgo);
        await AddShiftAsync(options, location, amy, Now.AddHours(30), publishedUtc: longAgo);                   // outside 24 h
        await AddShiftAsync(options, location, amy, Now.AddHours(10));                                         // draft
        await AddShiftAsync(options, location, amy, Now.AddHours(5), publishedUtc: longAgo, remindedFor: Now.AddHours(5)); // already reminded
        Shift moved = await AddShiftAsync(options, location, amy, Now.AddHours(8), publishedUtc: longAgo, remindedFor: Now.AddHours(6)); // moved since

        Result<List<ShiftReminderDue>> result = await GetRota(options, new()).GetShiftsDueForReminderAsync(Now);

        CollectionAssert.AreEquivalent(new[] { due.Id, moved.Id }, result.Data!.Select(x => x.Shift.Id).ToArray());
        Assert.IsTrue(result.Data!.All(x => x.SendEmail));
    }

    [TestMethod]
    public async Task Reminders_AShiftPublishedInsideItsWindowIsClaimedQuietly()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        await AddShiftAsync(options, location, amy, Now.AddHours(20), publishedUtc: Now.AddHours(-1));

        Result<List<ShiftReminderDue>> result = await GetRota(options, new()).GetShiftsDueForReminderAsync(Now);

        Assert.IsFalse(result.Data!.Single().SendEmail);
    }

    [TestMethod]
    public async Task Reminders_RespectTheCompanySwitchAndLeadTime()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile olly = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Olly");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = company.Id, SendShiftReminders = false });
            ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = other.Id, ShiftReminderLeadHours = 48 });
            await ctx.SaveChangesAsync();
        }

        await AddShiftAsync(options, location, amy, Now.AddHours(20), publishedUtc: Now.AddDays(-5));
        Shift olly40 = await AddShiftAsync(options, otherLocation, olly, Now.AddHours(40), publishedUtc: Now.AddDays(-5));

        Result<List<ShiftReminderDue>> result = await GetRota(options, new()).GetShiftsDueForReminderAsync(Now);

        Assert.AreEqual(olly40.Id, result.Data!.Single().Shift.Id);
    }

    [TestMethod]
    public async Task Reminders_ClaimOnlySucceedsOnceAndFailsIfTheShiftMoved()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        Shift shift = await AddShiftAsync(options, location, amy, Now.AddHours(20), publishedUtc: Now.AddDays(-5));
        RotaService rota = GetRota(options, new());

        Assert.IsTrue((await rota.ClaimShiftReminderAsync(shift.Id, shift.StartUtc)).IsSuccess);
        Assert.IsFalse((await rota.ClaimShiftReminderAsync(shift.Id, shift.StartUtc)).IsSuccess);
        Assert.IsFalse((await rota.ClaimShiftReminderAsync(shift.Id, shift.StartUtc.AddHours(1))).IsSuccess);
    }

    [TestMethod]
    public async Task ReminderSweep_QueuesEmailsForDueShiftsButNotQuietOnes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        await AddShiftAsync(options, location, amy, Now.AddHours(20), publishedUtc: Now.AddDays(-5));
        await AddShiftAsync(options, location, tom, Now.AddHours(20), publishedUtc: Now.AddHours(-1));

        RecordingNotificationDispatcher notifications = new();
        ShiftReminderHostedService sweep = new(Mock.Of<IServiceScopeFactory>(), notifications, new TestClock(Now), NullLogger<ShiftReminderHostedService>.Instance);
        RotaService rota = GetRota(options, new());

        int first = await sweep.RunSweepAsync(rota);
        int second = await sweep.RunSweepAsync(rota);

        Assert.AreEqual(1, first);
        Assert.AreEqual(0, second, "Both shifts were claimed by the first sweep.");
        NotificationJob job = notifications.Jobs.Single();
        Assert.AreEqual(amy.Id, job.UserId);
        Assert.AreEqual("Ipswich", ((ShiftReminderPayload)job.Payload).LocationName);
    }

    // --- Who's on today -----------------------------------------------------------------------

    [TestMethod]
    public async Task DayBoard_SortsTodaysShiftsByStatusAndShowsWhoIsClockedIn()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile early = await SchedulingTestHelpers.SeedUserAsync(options, location, "Early");
        UserProfile now = await SchedulingTestHelpers.SeedUserAsync(options, location, "Now");
        UserProfile late = await SchedulingTestHelpers.SeedUserAsync(options, location, "Late");
        UserProfile cover = await SchedulingTestHelpers.SeedUserAsync(options, location, "Cover");
        UserProfile forgot = await SchedulingTestHelpers.SeedUserAsync(options, location, "Forgot");
        DateTime published = Now.AddDays(-5);

        await AddShiftAsync(options, location, early, RotaTime.ToUtc(Today, new TimeOnly(7, 0)), 3, published);
        await AddShiftAsync(options, location, now, RotaTime.ToUtc(Today, new TimeOnly(10, 0)), 6, published);
        await AddShiftAsync(options, location, late, RotaTime.ToUtc(Today, new TimeOnly(17, 0)), 5, published);
        await AddShiftAsync(options, location, late, RotaTime.ToUtc(Today.AddDays(1), new TimeOnly(9, 0)), 5, published); // tomorrow

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.TimeEntries.Add(new TimeEntry { Id = Guid.NewGuid(), UserId = now.Id, LocationId = location.Id, ClockInUtc = Now.AddHours(-2), ClockInMethod = ClockMethod.Pin, IsActive = true });
            ctx.TimeEntries.Add(new TimeEntry { Id = Guid.NewGuid(), UserId = cover.Id, LocationId = location.Id, ClockInUtc = Now.AddHours(-1), ClockInMethod = ClockMethod.Manual, IsActive = true });
            ctx.TimeEntries.Add(new TimeEntry { Id = Guid.NewGuid(), UserId = forgot.Id, LocationId = location.Id, ClockInUtc = Now.AddHours(-20), ClockInMethod = ClockMethod.Pin, IsActive = true });
            await ctx.SaveChangesAsync();
        }

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        TestClock clock = new(Now);
        TimeEntryService service = new(factory, new ClockInPinService(factory, new PasswordHasher<UserProfile>()),
            new ClockInTerminalService(factory, clock, NullLogger<ClockInTerminalService>.Instance), new SchedulingSettingsService(factory),
            new TerminalEphemeralTokenService(clock), new TerminalEventBus(), clock, NullLogger<TimeEntryService>.Instance);

        Result<List<DayBoardEntry>> result = await service.GetDayBoardAsync(location.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        List<DayBoardEntry> board = result.Data!;
        Assert.AreEqual(4, board.Count, "Tomorrow's shift isn't on today's board, and nor is yesterday's forgotten clock-in.");
        Assert.AreEqual(DayBoardStatus.Finished, board.Single(x => x.UserId == early.Id).Status);
        Assert.AreEqual(DayBoardStatus.OnNow, board.Single(x => x.UserId == now.Id).Status);
        Assert.IsTrue(board.Single(x => x.UserId == now.Id).IsClockedIn);
        Assert.AreEqual(DayBoardStatus.Later, board.Single(x => x.UserId == late.Id).Status);
        Assert.AreEqual(DayBoardStatus.NoShift, board.Single(x => x.UserId == cover.Id).Status);
        Assert.AreEqual("Now", board.Single(x => x.UserId == now.Id).FirstName);
    }
}
