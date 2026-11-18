using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.OperationsAlerts;

[TestClass]
public class OperationsAlertWriterTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        DbContextOptions<ApplicationDbContext> Options,
        OperationsAlertWriter Writer,
        RecordingNotificationDispatcher Dispatcher,
        List<OperationsAlertChanged> Events,
        TestClock Clock,
        Company Company,
        Location Location);

    private static async Task<Harness> BuildAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        RecordingNotificationDispatcher dispatcher = new();
        OperationsAlertEventBus bus = new();
        List<OperationsAlertChanged> events = [];
        bus.OnChanged += events.Add;
        TestClock clock = new(Now);

        OperationsAlertWriter writer = new(new SystemDbContextFactory(options), dispatcher, bus, clock, NullLogger<OperationsAlertWriter>.Instance);

        return new Harness(options, writer, dispatcher, events, clock, company, location);
    }

    private static OperationsAlertRaise Raise(Harness h, string key = "automation-failed:abc", OperationsAlertSeverity severity = OperationsAlertSeverity.Warning,
        NotificationTopic? topic = NotificationTopic.AutomationRuleFailed, int? locationId = -1, string? detail = "1 of 1 actions failed") =>
        new(h.Company.Id, locationId == -1 ? h.Location.Id : locationId, OperationsAlertType.AutomationRuleFailed, severity, key,
            "Automation rule \"Lights\" failed", detail, "/automation", NotifyTopic: topic);

    private static async Task<List<OperationsAlert>> AlertsAsync(Harness h)
    {
        await using ApplicationDbContext ctx = new(h.Options);
        return await ctx.OperationsAlerts.OrderBy(x => x.RaisedAtUtc).ToListAsync();
    }

    [TestMethod]
    public async Task Raise_CreatesAnOpenAlertAndAnnouncesIt()
    {
        Harness h = await BuildAsync();

        Result<OperationsAlertRaiseOutcome> result = await h.Writer.RaiseAsync(Raise(h));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(OperationsAlertRaiseOutcome.Created, result.Data);

        OperationsAlert alert = (await AlertsAsync(h)).Single();
        Assert.AreEqual(h.Company.Id, alert.CompanyId);
        Assert.AreEqual(h.Location.Id, alert.LocationId);
        Assert.AreEqual(OperationsAlertStatus.Open, alert.Status);
        Assert.AreEqual(1, alert.OccurrenceCount);
        Assert.AreEqual(Now, alert.RaisedAtUtc);

        OperationsAlertChanged change = h.Events.Single();
        Assert.AreEqual(OperationsAlertChangeKind.Raised, change.Kind);
        Assert.AreEqual(h.Location.Id, change.LocationId);
    }

    [TestMethod]
    public async Task RaiseAgain_RefreshesTheSameAlertAndKeepsItsAcknowledgement()
    {
        Harness h = await BuildAsync();
        await h.Writer.RaiseAsync(Raise(h));

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            OperationsAlert alert = await ctx.OperationsAlerts.SingleAsync();
            alert.Status = OperationsAlertStatus.Acknowledged;
            alert.AcknowledgedAtUtc = Now;
            await ctx.SaveChangesAsync();
        }

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Result<OperationsAlertRaiseOutcome> again = await h.Writer.RaiseAsync(Raise(h, severity: OperationsAlertSeverity.Critical, detail: "2 of 2 actions failed"));

        Assert.AreEqual(OperationsAlertRaiseOutcome.Refreshed, again.Data);

        OperationsAlert refreshed = (await AlertsAsync(h)).Single();
        Assert.AreEqual(2, refreshed.OccurrenceCount);
        Assert.AreEqual(Now.AddMinutes(5), refreshed.LastSeenAtUtc);
        Assert.AreEqual(Now, refreshed.RaisedAtUtc);
        Assert.AreEqual(OperationsAlertStatus.Acknowledged, refreshed.Status);
        Assert.AreEqual(OperationsAlertSeverity.Critical, refreshed.Severity, "severity only climbs");
        Assert.AreEqual("2 of 2 actions failed", refreshed.Detail);
        Assert.AreEqual(OperationsAlertChangeKind.Refreshed, h.Events.Last().Kind);
    }

    [TestMethod]
    public async Task RaiseAfterResolve_StartsANewAlert()
    {
        Harness h = await BuildAsync();
        await h.Writer.RaiseAsync(Raise(h));
        await h.Writer.ResolveAsync(h.Company.Id, "automation-failed:abc");

        h.Clock.Advance(TimeSpan.FromHours(1));
        Result<OperationsAlertRaiseOutcome> again = await h.Writer.RaiseAsync(Raise(h));

        Assert.AreEqual(OperationsAlertRaiseOutcome.Created, again.Data);
        List<OperationsAlert> alerts = await AlertsAsync(h);
        Assert.HasCount(2, alerts);
        Assert.AreEqual(OperationsAlertStatus.Resolved, alerts[0].Status);
        Assert.AreEqual(OperationsAlertStatus.Open, alerts[1].Status);
    }

    [TestMethod]
    public async Task Resolve_MarksTheAlertClearedAutomatically()
    {
        Harness h = await BuildAsync();
        await h.Writer.RaiseAsync(Raise(h));
        h.Clock.Advance(TimeSpan.FromMinutes(3));

        Result<bool> result = await h.Writer.ResolveAsync(h.Company.Id, "automation-failed:abc", "The rule ran without errors.");

        Assert.IsTrue(result.Data);
        OperationsAlert alert = (await AlertsAsync(h)).Single();
        Assert.AreEqual(OperationsAlertStatus.Resolved, alert.Status);
        Assert.AreEqual(OperationsAlertResolution.Auto, alert.Resolution);
        Assert.AreEqual(Now.AddMinutes(3), alert.ResolvedAtUtc);
        Assert.AreEqual("The rule ran without errors.", alert.ResolutionNote);
        Assert.IsNull(alert.ResolvedByUserId);
        Assert.AreEqual(OperationsAlertChangeKind.Resolved, h.Events.Last().Kind);
    }

    [TestMethod]
    public async Task Resolve_WithNothingOpen_DoesNothing()
    {
        Harness h = await BuildAsync();

        Result<bool> result = await h.Writer.ResolveAsync(h.Company.Id, "kiosk-offline:nothing");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);
        Assert.IsEmpty(h.Events);
    }

    [TestMethod]
    public async Task Resolve_OnlyTouchesItsOwnCompany()
    {
        Harness h = await BuildAsync();
        await h.Writer.RaiseAsync(Raise(h));

        Result<bool> result = await h.Writer.ResolveAsync(h.Company.Id + 100, "automation-failed:abc");

        Assert.IsFalse(result.Data);
        Assert.AreEqual(OperationsAlertStatus.Open, (await AlertsAsync(h)).Single().Status);
    }

    [TestMethod]
    public async Task Raise_WithoutACompany_IsRefused()
    {
        // A system-tenant write with CompanyId 0 would otherwise be stamped as Play2Day.
        Harness h = await BuildAsync();

        Result<OperationsAlertRaiseOutcome> result = await h.Writer.RaiseAsync(Raise(h) with { CompanyId = 0 });

        Assert.IsFalse(result.IsSuccess);
        Assert.IsEmpty(await AlertsAsync(h));
    }

    [TestMethod]
    public async Task Raise_IsSkippedWhenTheFeatureIsOff()
    {
        Harness h = await BuildAsync();

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = h.Company.Id, Feature = CompanyFeature.OperationsAlerts, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Result<OperationsAlertRaiseOutcome> result = await h.Writer.RaiseAsync(Raise(h));

        Assert.AreEqual(OperationsAlertRaiseOutcome.Skipped, result.Data);
        Assert.IsEmpty(await AlertsAsync(h));
        Assert.IsEmpty(h.Dispatcher.Jobs);
    }

    [TestMethod]
    public async Task Raise_IsSkippedForADemoCompany()
    {
        Harness h = await BuildAsync();

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Company company = await ctx.Companies.SingleAsync();
            company.IsDemo = true;
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(OperationsAlertRaiseOutcome.Skipped, (await h.Writer.RaiseAsync(Raise(h))).Data);
    }

    [TestMethod]
    public async Task Notify_GoesToAdminsAndTheLocationsManagersOnlyWhenTheAlertIsNew()
    {
        Harness h = await BuildAsync();
        UserProfile admin = await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Ada", "Admin");
        UserProfile manager = await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Mia", "Manager");
        await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Sam", "Staff");

        await h.Writer.RaiseAsync(Raise(h));
        await h.Writer.RaiseAsync(Raise(h));

        CollectionAssert.AreEquivalent(new[] { admin.Id, manager.Id }, h.Dispatcher.Jobs.Select(x => x.UserId).ToList());
        Assert.IsTrue(h.Dispatcher.Jobs.All(x => x.Topic == NotificationTopic.AutomationRuleFailed));

        OperationsAlertPayload payload = (OperationsAlertPayload)h.Dispatcher.Jobs[0].Payload;
        Assert.AreEqual("Ipswich", payload.LocationName);
        Assert.AreEqual("/manage/alerts", payload.Url);
    }

    [TestMethod]
    public async Task Notify_ACompanyWideAlertGoesToAdminsOnly()
    {
        Harness h = await BuildAsync();
        UserProfile admin = await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Ada", "Admin");
        await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Mia", "Manager");

        await h.Writer.RaiseAsync(Raise(h, locationId: null));

        Assert.AreEqual(admin.Id, h.Dispatcher.Jobs.Single().UserId);
    }

    [TestMethod]
    public async Task Notify_IsLeftToTheProducerWhenNoTopicIsGiven()
    {
        Harness h = await BuildAsync();
        await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Ada", "Admin");

        await h.Writer.RaiseAsync(Raise(h, topic: null));

        Assert.IsEmpty(h.Dispatcher.Jobs);
    }

    [TestMethod]
    public async Task Notify_NamesThePersonTheAlertIsAboutWithoutStoringTheName()
    {
        Harness h = await BuildAsync();
        await OperationsAlertTestSupport.SeedPersonAsync(h.Options, h.Location, "Ada", "Admin");
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");

        await h.Writer.RaiseAsync(Raise(h) with { SubjectUserId = staff.Id, Title = "Missed clock-in", Detail = "Shift started at 09:00." });

        Assert.AreEqual("Sam Tester", ((OperationsAlertPayload)h.Dispatcher.Jobs.Single().Payload).SubjectName);

        OperationsAlert alert = (await AlertsAsync(h)).Single();
        Assert.DoesNotContain("Sam", alert.Title);
        Assert.DoesNotContain("Sam", alert.Detail!);
    }
}
