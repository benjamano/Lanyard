using Lanyard.Application.Services.OperationsAlerts;
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
public class ComplianceAlertSweepTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required ComplianceAlertSweepHostedService Sweep { get; init; }
        public required RecordingNotificationDispatcher Dispatcher { get; init; }
        public required TestClock Clock { get; init; }
        public required Company Company { get; init; }
        public required Location Location { get; init; }
        public required UserProfile Manager { get; init; }

        public async Task<List<OperationsAlert>> AlertsAsync()
        {
            await using ApplicationDbContext ctx = new(Options);
            return await ctx.OperationsAlerts.OrderBy(x => x.DedupKey).ToListAsync();
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
            Sweep = new ComplianceAlertSweepHostedService(factory, writer, dispatcher, clock, NullLogger<ComplianceAlertSweepHostedService>.Instance),
            Dispatcher = dispatcher,
            Clock = clock,
            Company = company,
            Location = location,
            Manager = await OperationsAlertTestSupport.SeedPersonAsync(options, location, "Mia", "Manager")
        };
    }

    private static async Task<CourseAssignment> SeedAssignmentAsync(Harness h, UserProfile user, DateTime? due, DateTime? completed = null)
    {
        await using ApplicationDbContext ctx = new(h.Options);

        Course course = new() { Id = Guid.NewGuid(), CompanyId = h.Company.Id, Name = "Fire safety", IsActive = true };
        CourseAssignment assignment = new()
        {
            Id = Guid.NewGuid(), CourseId = course.Id, UserId = user.Id, AssignedDate = Now.AddDays(-30),
            DueDate = due, CompletedDate = completed, IsActive = true
        };

        ctx.Courses.Add(course);
        ctx.CourseAssignments.Add(assignment);
        await ctx.SaveChangesAsync();

        return assignment;
    }

    private static async Task<StaffDocument> SeedDocumentAsync(Harness h, UserProfile user, DateTime expiry, DateTime? reminder)
    {
        await using ApplicationDbContext ctx = new(h.Options);

        StaffDocumentType type = new() { Id = Guid.NewGuid(), CompanyId = h.Company.Id, Name = "DBS check", RequiresExpiryDate = true };
        StaffDocument document = new()
        {
            Id = Guid.NewGuid(), CompanyId = h.Company.Id, UserId = user.Id, StaffDocumentTypeId = type.Id, FileMetadataId = Guid.NewGuid(),
            ExpiryDate = expiry, ReminderDate = reminder, UploadedDate = Now.AddYears(-1), UploadedByUserId = h.Manager.Id
        };

        ctx.StaffDocumentTypes.Add(type);
        ctx.StaffDocuments.Add(document);
        await ctx.SaveChangesAsync();

        return document;
    }

    [TestMethod]
    public async Task OverdueTraining_RaisesAWarningAtThePersonsLocation_AndCompletingItClearsIt()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        CourseAssignment assignment = await SeedAssignmentAsync(h, sam, Now.AddDays(-2));
        await SeedAssignmentAsync(h, sam, Now.AddDays(5));
        await SeedAssignmentAsync(h, sam, Now.AddDays(-5), completed: Now.AddDays(-6));

        await h.Sweep.RunSweepAsync();

        OperationsAlert alert = (await h.AlertsAsync()).Single();
        Assert.AreEqual($"training-overdue:{assignment.Id:N}", alert.DedupKey);
        Assert.AreEqual(OperationsAlertSeverity.Warning, alert.Severity);
        Assert.AreEqual(h.Location.Id, alert.LocationId);
        Assert.AreEqual(sam.Id, alert.SubjectUserId);
        Assert.AreEqual("Training overdue: Fire safety", alert.Title);

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            (await ctx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id)).CompletedDate = Now;
            await ctx.SaveChangesAsync();
        }

        (_, int cleared) = await h.Sweep.RunSweepAsync();

        Assert.AreEqual(1, cleared);
        Assert.AreEqual(OperationsAlertStatus.Resolved, (await h.AlertsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task Documents_AreInfoWhenDueForRenewal_ThenTheSameAlertBecomesExpired()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        StaffDocument document = await SeedDocumentAsync(h, sam, expiry: Now.AddDays(10), reminder: Now.AddDays(-1));
        await SeedDocumentAsync(h, sam, expiry: Now.AddDays(60), reminder: Now.AddDays(30));

        await h.Sweep.RunSweepAsync();

        OperationsAlert expiring = (await h.AlertsAsync()).Single();
        Assert.AreEqual(OperationsAlertType.StaffDocumentExpiring, expiring.Type);
        Assert.AreEqual(OperationsAlertSeverity.Info, expiring.Severity);
        Assert.AreEqual("DBS check expiring", expiring.Title);

        h.Clock.Advance(TimeSpan.FromDays(11));
        await h.Sweep.RunSweepAsync();

        OperationsAlert expired = (await h.AlertsAsync()).Single();
        Assert.AreEqual(expiring.Id, expired.Id, "the same alert, moved up");
        Assert.AreEqual(OperationsAlertType.StaffDocumentExpired, expired.Type);
        Assert.AreEqual(OperationsAlertSeverity.Warning, expired.Severity);

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            (await ctx.StaffDocuments.SingleAsync(x => x.Id == document.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        await h.Sweep.RunSweepAsync();

        Assert.AreEqual(OperationsAlertStatus.Resolved, (await h.AlertsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task ManyNewAlerts_SendOneSummaryPerLocationAndTopic()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        UserProfile kim = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Kim");
        await SeedAssignmentAsync(h, sam, Now.AddDays(-2));
        await SeedAssignmentAsync(h, sam, Now.AddDays(-3));
        await SeedAssignmentAsync(h, kim, Now.AddDays(-4));

        await h.Sweep.RunSweepAsync();
        await h.Sweep.RunSweepAsync();

        NotificationJob job = h.Dispatcher.Jobs.Single();
        Assert.AreEqual(h.Manager.Id, job.UserId);
        Assert.AreEqual(NotificationTopic.TrainingOverdue, job.Topic);

        OperationsAlertPayload payload = (OperationsAlertPayload)job.Payload;
        Assert.AreEqual("3 training courses are overdue", payload.Title);
        Assert.Contains("2 people", payload.Detail!);
    }

    [TestMethod]
    public async Task ASingleNewAlert_NamesThePersonInTheNotificationOnly()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        await SeedAssignmentAsync(h, sam, Now.AddDays(-2));

        await h.Sweep.RunSweepAsync();

        OperationsAlertPayload payload = (OperationsAlertPayload)h.Dispatcher.Jobs.Single().Payload;
        Assert.AreEqual("Sam Tester", payload.SubjectName);
        Assert.DoesNotContain("Sam", (await h.AlertsAsync()).Single().Title);
    }

    [TestMethod]
    public async Task PeopleWhoHaveLeftEveryLocation_AreNotChased()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        await SeedAssignmentAsync(h, sam, Now.AddDays(-2));

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships.Where(x => x.UserId == sam.Id));
            await ctx.SaveChangesAsync();
        }

        await h.Sweep.RunSweepAsync();

        Assert.IsEmpty(await h.AlertsAsync());
    }

    [TestMethod]
    public async Task SwitchingTrainingOff_ClearsItsAlerts()
    {
        Harness h = await BuildAsync();
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(h.Options, h.Location, "Sam");
        await SeedAssignmentAsync(h, sam, Now.AddDays(-2));
        await h.Sweep.RunSweepAsync();

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = h.Company.Id, Feature = CompanyFeature.Training, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        await h.Sweep.RunSweepAsync();

        Assert.AreEqual(OperationsAlertStatus.Resolved, (await h.AlertsAsync()).Single().Status);
    }
}
