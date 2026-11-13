using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.OperationsAlerts;

[TestClass]
public class MissedClockInAlertTests
{
    // Friday 13 November 2026, 09:20 UTC (09:20 at the venue: GMT).
    private static readonly DateTime Now = new(2026, 11, 13, 9, 20, 0, DateTimeKind.Utc);
    private static readonly DateTime ShiftStart = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required MissedClockInAlertHostedService Sweep { get; init; }
        public required RecordingNotificationDispatcher Dispatcher { get; init; }
        public required TestClock Clock { get; init; }
        public required Company Company { get; init; }
        public required Location Location { get; init; }
        public required UserProfile Staff { get; init; }
        public required UserProfile Manager { get; init; }

        public async Task<List<OperationsAlert>> AlertsAsync()
        {
            await using ApplicationDbContext ctx = new(Options);
            return await ctx.OperationsAlerts.ToListAsync();
        }
    }

    private static async Task<Harness> BuildAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        TestClock clock = new(Now);
        RecordingNotificationDispatcher dispatcher = new();
        SystemDbContextFactory factory = new(options);
        OperationsAlertWriter writer = new(factory, dispatcher, new OperationsAlertEventBus(), clock, NullLogger<OperationsAlertWriter>.Instance);

        return new Harness
        {
            Options = options,
            Sweep = new MissedClockInAlertHostedService(factory, writer, new TerminalEventBus(), clock, NullLogger<MissedClockInAlertHostedService>.Instance),
            Dispatcher = dispatcher,
            Clock = clock,
            Company = company,
            Location = location,
            Staff = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam"),
            Manager = await OperationsAlertTestSupport.SeedPersonAsync(options, location, "Mia", "Manager")
        };
    }

    private static async Task<Shift> SeedShiftAsync(Harness h, bool published = true, string? userId = "staff", int? locationId = null)
    {
        await using ApplicationDbContext ctx = new(h.Options);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = locationId ?? h.Location.Id,
            UserId = userId == "staff" ? h.Staff.Id : userId,
            StartUtc = ShiftStart,
            EndUtc = ShiftStart.AddHours(8),
            PublishedDateUtc = published ? ShiftStart.AddDays(-7) : null,
            CreateByUserId = "seed",
            CreateDate = ShiftStart.AddDays(-8)
        };

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task ClockInAsync(Harness h, DateTime at, int? locationId = null)
    {
        await using ApplicationDbContext ctx = new(h.Options);
        ctx.TimeEntries.Add(new TimeEntry { Id = Guid.NewGuid(), UserId = h.Staff.Id, LocationId = locationId ?? h.Location.Id, ClockInUtc = at, CreateDate = at });
        await ctx.SaveChangesAsync();
    }

    private static async Task SetGraceAsync(Harness h, int minutes)
    {
        await using ApplicationDbContext ctx = new(h.Options);
        ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = h.Company.Id, MissedClockInAlertMinutes = minutes });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task NobodyClockedIn_PastTheGrace_RaisesOneAlertAboutThePerson()
    {
        Harness h = await BuildAsync();
        Shift shift = await SeedShiftAsync(h);

        await h.Sweep.RunSweepAsync();
        await h.Sweep.RunSweepAsync();

        OperationsAlert alert = (await h.AlertsAsync()).Single();
        Assert.AreEqual($"missed-clock-in:{shift.Id:N}", alert.DedupKey);
        Assert.AreEqual(OperationsAlertType.MissedClockIn, alert.Type);
        Assert.AreEqual(h.Staff.Id, alert.SubjectUserId);
        Assert.AreEqual(h.Location.Id, alert.LocationId);
        Assert.DoesNotContain("Sam", alert.Title + alert.Detail);

        NotificationJob job = h.Dispatcher.Jobs.Single();
        Assert.AreEqual(h.Manager.Id, job.UserId);
        Assert.AreEqual(NotificationTopic.MissedClockIn, job.Topic);
        Assert.AreEqual("Sam Tester", ((OperationsAlertPayload)job.Payload).SubjectName);
    }

    [TestMethod]
    public async Task WithinTheGrace_NothingIsRaised()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        h.Clock.UtcNow = ShiftStart.AddMinutes(14);

        await h.Sweep.RunSweepAsync();

        Assert.IsEmpty(await h.AlertsAsync());
    }

    [TestMethod]
    public async Task TheCompanysOwnGraceIsUsed_AndZeroTurnsItOff()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        await SetGraceAsync(h, 30);

        await h.Sweep.RunSweepAsync();
        Assert.IsEmpty(await h.AlertsAsync(), "20 minutes in, under a 30-minute grace");

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            (await ctx.CompanySchedulingSettings.SingleAsync()).MissedClockInAlertMinutes = 0;
            await ctx.SaveChangesAsync();
        }

        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.Sweep.RunSweepAsync();
        Assert.IsEmpty(await h.AlertsAsync());
    }

    [TestMethod]
    public async Task UnpublishedAndOpenShifts_AreIgnored()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h, published: false);
        await SeedShiftAsync(h, userId: null);

        await h.Sweep.RunSweepAsync();

        Assert.IsEmpty(await h.AlertsAsync());
    }

    [TestMethod]
    public async Task ClockedInEarlyWithinTheWindow_Counts()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        await ClockInAsync(h, ShiftStart.AddMinutes(-10));

        await h.Sweep.RunSweepAsync();

        Assert.IsEmpty(await h.AlertsAsync());
    }

    [TestMethod]
    public async Task ClockingInAtAnotherLocation_DoesNotCount()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        await ClockInAsync(h, ShiftStart, locationId: h.Location.Id + 50);

        await h.Sweep.RunSweepAsync();

        Assert.HasCount(1, await h.AlertsAsync());
    }

    [TestMethod]
    public async Task ClockingInLate_ClearsTheAlert()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        await h.Sweep.RunSweepAsync();

        await ClockInAsync(h, Now.AddMinutes(4));
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        (int raised, int cleared) = await h.Sweep.RunSweepAsync();

        Assert.AreEqual(0, raised);
        Assert.AreEqual(1, cleared);
        OperationsAlert alert = (await h.AlertsAsync()).Single();
        Assert.AreEqual(OperationsAlertStatus.Resolved, alert.Status);
        Assert.AreEqual(OperationsAlertResolution.Auto, alert.Resolution);
        Assert.AreEqual("Clocked in at 09:24.", alert.ResolutionNote);
    }

    [TestMethod]
    public async Task ReassigningTheShift_ClearsTheAlert()
    {
        Harness h = await BuildAsync();
        Shift shift = await SeedShiftAsync(h);
        await h.Sweep.RunSweepAsync();

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            (await ctx.Shifts.SingleAsync(x => x.Id == shift.Id)).UserId = h.Manager.Id;
            await ctx.SaveChangesAsync();
        }

        await h.Sweep.RunSweepAsync();

        Assert.IsTrue((await h.AlertsAsync()).Any(x => x.SubjectUserId == h.Staff.Id && x.Status == OperationsAlertStatus.Resolved));
    }

    [TestMethod]
    public async Task AShiftThatEndsWithNobodyClockedIn_StaysOpen()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);
        await h.Sweep.RunSweepAsync();

        h.Clock.UtcNow = ShiftStart.AddHours(10);
        await h.Sweep.RunSweepAsync();

        Assert.AreEqual(OperationsAlertStatus.Open, (await h.AlertsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task CompaniesWithoutShiftManagement_AreSkipped()
    {
        Harness h = await BuildAsync();
        await SeedShiftAsync(h);

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = h.Company.Id, Feature = CompanyFeature.StaffScheduling, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        await h.Sweep.RunSweepAsync();

        Assert.IsEmpty(await h.AlertsAsync());
    }
}
