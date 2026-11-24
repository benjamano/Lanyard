using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.OperationsAlerts;
using Lanyard.Tests.Services.Scheduling;
using Lanyard.Tests.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Checklists;

// The missed-checklist sweep, through the real operations alert writer, so the alert row and who
// is notified are both checked.
[TestClass]
public class ChecklistMissedAlertTests
{
    // Saturday 21 Nov 2026 (GMT). Opening is due at 10:15.
    private static readonly DateTime Today1030 = new(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private sealed class Harness
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required ChecklistMissedAlertHostedService Sweep { get; init; }
        public required RecordingNotificationDispatcher Dispatcher { get; init; }
        public required TestClock Clock { get; init; }
        public required Company Company { get; init; }
        public required Location Ipswich { get; init; }
        public required Location Wisbech { get; init; }
        public required UserProfile IpswichManager { get; init; }
        public required UserProfile WisbechManager { get; init; }
        public required Checklist Opening { get; init; }

        public async Task<List<OperationsAlert>> AlertsAsync()
        {
            await using ApplicationDbContext ctx = new(Options);
            return await ctx.OperationsAlerts.ToListAsync();
        }
    }

    private static async Task<Harness> BuildAsync(DateTime now, ChecklistDueRule rule = ChecklistDueRule.FixedTime,
        Func<IOperationsAlertWriter, IOperationsAlertWriter>? wrapWriter = null)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location wisbech;
        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
            await TestFeatures.SwitchOnEverywhereAsync(ctx);
        }

        Checklist opening = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            LocationId = ipswich.Id,
            Name = "Opening",
            DueRule = rule,
            DueTime = rule == ChecklistDueRule.FixedTime ? new TimeOnly(10, 15) : null,
            CreateDate = now.AddDays(-5),
            Tasks =
            [
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Unlock", SortOrder = 0 },
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Lights", SortOrder = 1 }
            ]
        };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Checklists.Add(opening);
            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(now);
        RecordingNotificationDispatcher dispatcher = new();
        SystemDbContextFactory factory = new(options);
        OperationsAlertWriter writer = new(factory, dispatcher, new OperationsAlertEventBus(), clock, NullLogger<OperationsAlertWriter>.Instance);

        return new Harness
        {
            Options = options,
            Sweep = new ChecklistMissedAlertHostedService(factory, wrapWriter?.Invoke(writer) ?? writer, clock, NullLogger<ChecklistMissedAlertHostedService>.Instance),
            Dispatcher = dispatcher,
            Clock = clock,
            Company = company,
            Ipswich = ipswich,
            Wisbech = wisbech,
            IpswichManager = await OperationsAlertTestSupport.SeedPersonAsync(options, ipswich, "Mia", "Manager"),
            WisbechManager = await OperationsAlertTestSupport.SeedPersonAsync(options, wisbech, "Will", "Manager"),
            Opening = opening
        };
    }

    private static async Task TickAsync(Harness h, DateOnly day, int count)
    {
        await using ApplicationDbContext ctx = new(h.Options);

        List<ChecklistRunItem> items = await ctx.ChecklistRunItems
            .Where(x => x.Run!.ChecklistId == h.Opening.Id && x.Run.VenueDate == day)
            .OrderBy(x => x.SortOrder)
            .Take(count)
            .ToListAsync();

        foreach (ChecklistRunItem item in items)
        {
            ctx.ChecklistCompletions.Add(new ChecklistCompletion { RunItemId = item.Id, UserId = h.IpswichManager.Id, CompletedUtc = h.Clock.UtcNow, Method = ChecklistCompletionMethod.Phone });
        }

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task PastDueWithTasksLeft_RaisesOnce_AndTellsOnlyThatLocationsManagers()
    {
        Harness h = await BuildAsync(Today1030);

        (int raised, _) = await h.Sweep.RunSweepAsync();
        (int again, _) = await h.Sweep.RunSweepAsync();

        Assert.AreEqual(2, raised, "Today's run and yesterday's (nobody opened it) are both missed; older days are history only.");
        Assert.AreEqual(0, again, "A run is only alerted once.");

        string todayKey = (await RunIdAsync(h, Today)).ToString("N");
        List<OperationsAlert> alerts = await h.AlertsAsync();
        OperationsAlert alert = alerts.Single(x => x.DedupKey.EndsWith(todayKey));
        Assert.AreEqual(OperationsAlertType.ChecklistMissed, alert.Type);
        Assert.AreEqual(h.Ipswich.Id, alert.LocationId);
        Assert.AreEqual("Opening not finished", alert.Title);
        StringAssert.Contains(alert.Detail, "0 of 2 tasks done by 10:15");

        List<string> told = [.. h.Dispatcher.Jobs.Where(x => x.Topic == NotificationTopic.ChecklistMissed).Select(x => x.UserId).Distinct()];
        CollectionAssert.Contains(told, h.IpswichManager.Id);
        CollectionAssert.DoesNotContain(told, h.WisbechManager.Id);
    }

    [TestMethod]
    public async Task NotDueYet_OrFinishedInTime_RaisesNothing()
    {
        Harness h = await BuildAsync(new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc));

        // Set up this morning, so there's no yesterday to miss.
        await using (ApplicationDbContext ctx = new(h.Options))
        {
            (await ctx.Checklists.SingleAsync()).CreateDate = new DateTime(2026, 11, 21, 7, 0, 0, DateTimeKind.Utc);
            await ctx.SaveChangesAsync();
        }

        (int early, _) = await h.Sweep.RunSweepAsync();
        Assert.AreEqual(0, early, "Not due until 10:15.");
        await TickAsync(h, Today, 2);

        h.Clock.UtcNow = Today1030;
        (int raised, _) = await h.Sweep.RunSweepAsync();

        Assert.AreEqual(0, raised);
        Assert.AreEqual(0, (await h.AlertsAsync()).Count(x => x.Type == OperationsAlertType.ChecklistMissed));
    }

    [TestMethod]
    public async Task FinishedLate_ClearsTheAlert()
    {
        Harness h = await BuildAsync(Today1030);
        await h.Sweep.RunSweepAsync();
        await TickAsync(h, Today, 2);
        await TickAsync(h, Today.AddDays(-1), 2);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        (_, int cleared) = await h.Sweep.RunSweepAsync();

        Assert.IsTrue(cleared >= 1);

        string todayKey = (await RunIdAsync(h, Today)).ToString("N");
        OperationsAlert alert = (await h.AlertsAsync()).Single(x => x.DedupKey.EndsWith(todayKey));
        Assert.AreEqual(OperationsAlertStatus.Resolved, alert.Status);
        Assert.AreEqual(OperationsAlertResolution.Auto, alert.Resolution);
    }

    [TestMethod]
    public async Task NoDueTime_IsMissedOnceTheDayIsOver_EvenIfNobodyOpenedIt()
    {
        // 08:00 on the 21st: the 20th is over and nobody looked at its checklist.
        Harness h = await BuildAsync(new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc), ChecklistDueRule.None);

        (int raised, _) = await h.Sweep.RunSweepAsync();

        Assert.AreEqual(1, raised, "Yesterday's is missed; today's isn't over yet; older days are history only.");
        OperationsAlert alert = (await h.AlertsAsync()).Single(x => x.Type == OperationsAlertType.ChecklistMissed);
        StringAssert.Contains(alert.Detail, "by the end of Fri 20 Nov");
    }

    [TestMethod]
    public async Task FeatureOff_OrDemoCompany_RaisesNothing()
    {
        Harness off = await BuildAsync(Today1030);

        await using (ApplicationDbContext ctx = new(off.Options))
        {
            LocationFeatureSetting checklists = await ctx.LocationFeatureSettings
                .SingleAsync(x => x.LocationId == off.Ipswich.Id && x.Feature == CompanyFeature.Checklists);
            checklists.IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        Harness demo = await BuildAsync(Today1030);

        await using (ApplicationDbContext ctx = new(demo.Options))
        {
            (await ctx.Companies.SingleAsync(x => x.Id == demo.Company.Id)).IsDemo = true;
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(0, (await off.Sweep.RunSweepAsync()).Raised);
        Assert.AreEqual(0, (await demo.Sweep.RunSweepAsync()).Raised);
    }

    private static async Task<Guid> RunIdAsync(Harness h, DateOnly day)
    {
        await using ApplicationDbContext ctx = new(h.Options);
        return await ctx.ChecklistRuns.Where(x => x.ChecklistId == h.Opening.Id && x.VenueDate == day).Select(x => x.Id).SingleAsync();
    }

    // Fails the first RaiseAsync, then hands everything to the real writer.
    private sealed class FailsOnceWriter(IOperationsAlertWriter inner) : IOperationsAlertWriter
    {
        public int Attempts { get; private set; }

        public Task<Lanyard.Infrastructure.DTO.Result<OperationsAlertRaiseOutcome>> RaiseAsync(OperationsAlertRaise raise, CancellationToken cancellationToken = default) =>
            ++Attempts == 1
                ? Task.FromResult(Lanyard.Infrastructure.DTO.Result<OperationsAlertRaiseOutcome>.Fail("database blip"))
                : inner.RaiseAsync(raise, cancellationToken);

        public Task<Lanyard.Infrastructure.DTO.Result<bool>> ResolveAsync(int companyId, string dedupKey, string? note = null, CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(companyId, dedupKey, note, cancellationToken);
    }

    [TestMethod]
    public async Task ARaiseThatFails_IsTriedAgainOnTheNextSweep()
    {
        FailsOnceWriter? flaky = null;
        Harness h = await BuildAsync(Today1030, wrapWriter: inner => flaky = new FailsOnceWriter(inner));

        // Yesterday's run may be missed as well; whichever run's raise failed must get no alert.
        await h.Sweep.RunSweepAsync();
        Assert.AreEqual(flaky!.Attempts - 1, (await h.AlertsAsync()).Count, "The failed raise left no alert.");

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Assert.IsTrue(await ctx.ChecklistRuns.AnyAsync(x => x.MissedAlertUtc == null), "The failed run's claim was handed back.");
        }

        await h.Sweep.RunSweepAsync();

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Assert.IsFalse(await ctx.ChecklistRuns.AnyAsync(x => x.MissedAlertUtc == null), "Every missed run is now claimed.");
        }

        Assert.AreEqual(flaky.Attempts - 1, (await h.AlertsAsync()).Count, "Every successful raise made one alert, including the retry.");
    }
}
