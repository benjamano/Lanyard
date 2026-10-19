using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskAlertMonitorTests
{
    // Thursday 15 October 2026, 13:00 UTC (14:00 at the venue, British Summer Time).
    private static readonly DateTime Now = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);

    private const string ConnectionId = "kiosk-connection";

    private static readonly KioskHealthOptions HealthOptions = new();

    private sealed class Harness
    {
        public required KioskAlertMonitorHostedService Monitor { get; init; }
        public required TestClock Clock { get; init; }
        public required RecordingNotificationDispatcher Dispatcher { get; init; }
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required Company Company { get; init; }
        public required Location Location { get; init; }
        public List<string> Connected { get; } = [];

        public Task<int> SweepAsync() => Monitor.RunSweepAsync(SchedulingTestHelpers.GetFactory(Options), Connected);

        public List<NotificationJob> JobsFor(string userId) => Dispatcher.Jobs.Where(x => x.UserId == userId).ToList();
    }

    // The monitor starts well before "now", so its restart grace is over unless a test wants it.
    private static async Task<Harness> BuildAsync(DateTime? startedAt = null)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        TestClock clock = new(startedAt ?? Now - TimeSpan.FromHours(1));
        RecordingNotificationDispatcher dispatcher = new();

        KioskAlertMonitorHostedService monitor = new(Mock.Of<IServiceScopeFactory>(), dispatcher,
            Microsoft.Extensions.Options.Options.Create(HealthOptions), clock, NullLogger<KioskAlertMonitorHostedService>.Instance);

        clock.UtcNow = startedAt ?? Now;

        return new Harness { Monitor = monitor, Clock = clock, Dispatcher = dispatcher, Options = options, Company = company, Location = location };
    }

    private static async Task<Client> SeedKioskAsync(Harness harness, DateTime lastHeartbeat, bool withLocation = true,
        DateTime? lastDisconnect = null, bool dmxBroken = false, double cpu = 10, Company? company = null)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        Client client = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = (company ?? harness.Company).Id,
            Name = "Arena PC",
            LocationId = withLocation ? harness.Location.Id : null,
            MostRecentConnectionId = ConnectionId,
            LastDisconnectDate = lastDisconnect
        };

        ctx.Clients.Add(client);
        ctx.ClientHealthSnapshots.Add(new ClientHealthSnapshot
        {
            ClientId = client.Id,
            FirstHeartbeatUtc = lastHeartbeat - TimeSpan.FromDays(1),
            LastHeartbeatUtc = lastHeartbeat,
            DmxConfigured = dmxBroken,
            DmxOpen = false,
            CpuPercent = cpu,
            OfflineFallbackReady = true
        });

        await ctx.SaveChangesAsync();

        return client;
    }

    private static async Task HeartbeatAsync(Harness harness, Guid clientId, Action<ClientHealthSnapshot>? change = null)
    {
        await using ApplicationDbContext ctx = new(harness.Options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync(x => x.ClientId == clientId);
        snapshot.LastHeartbeatUtc = harness.Clock.UtcNow;
        change?.Invoke(snapshot);
        await ctx.SaveChangesAsync();
    }

    private static async Task<UserProfile> SeedPersonAsync(Harness harness, string name, string role, Location? location = null, DateTimeOffset? lockedUntil = null)
    {
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(harness.Options, location ?? harness.Location, name);

        await using ApplicationDbContext ctx = new(harness.Options);

        ApplicationRole? existing = await ctx.Roles.FirstOrDefaultAsync(x => x.Name == role);
        ApplicationRole applicationRole = existing ?? new ApplicationRole
        {
            Id = Guid.NewGuid().ToString(), Name = role, NormalizedName = role.ToUpperInvariant(), CreatedByUserId = "seed", IsActive = true
        };

        if (existing == null)
        {
            ctx.Roles.Add(applicationRole);
        }

        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = applicationRole.Id });

        if (lockedUntil != null)
        {
            UserProfile tracked = await ctx.Users.SingleAsync(x => x.Id == user.Id);
            tracked.LockoutEnd = lockedUntil;
        }

        await ctx.SaveChangesAsync();

        return user;
    }

    private static async Task SetOpeningHoursAsync(Harness harness, TimeOnly opens, TimeOnly closes)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            ctx.LocationOpeningHours.Add(new LocationOpeningHours
            {
                LocationId = harness.Location.Id, DayOfWeek = day, OpensAt = opens, ClosesAt = closes, IsActive = true, CreateDate = Now
            });
        }

        await ctx.SaveChangesAsync();
    }

    private static TimeSpan Silent => HealthOptions.OfflineAfter + TimeSpan.FromMinutes(1);

    [TestMethod]
    public async Task Offline_AlertsTheManagerAndAdminOnce()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        UserProfile admin = await SeedPersonAsync(harness, "Ada", "Admin");
        Client kiosk = await SeedKioskAsync(harness, Now - Silent);

        await harness.SweepAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.SweepAsync();

        NotificationJob managerJob = harness.JobsFor(manager.Id).Single();
        NotificationJob adminJob = harness.JobsFor(admin.Id).Single();
        Assert.AreEqual(NotificationTopic.KioskOffline, managerJob.Topic);

        KioskOfflinePayload payload = (KioskOfflinePayload)managerJob.Payload;
        Assert.AreEqual(kiosk.Id, payload.ClientId);
        Assert.AreEqual("Ipswich", payload.LocationName);
        Assert.AreEqual("13:52", payload.DownSince);
        Assert.IsFalse(payload.ShutDownCleanly);
        Assert.IsTrue(payload.OfflineCopyReady);
        Assert.AreEqual(KioskNotices.ManagerUrl, payload.Url);
        Assert.AreEqual(KioskNotices.AdminUrl, ((KioskOfflinePayload)adminJob.Payload).Url);
    }

    [TestMethod]
    public async Task Offline_WaitsOutTheRestartGrace()
    {
        // Straight after a redeploy every kiosk looks silent; none of them should page anyone.
        Harness harness = await BuildAsync(startedAt: Now);
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        await SeedKioskAsync(harness, Now - Silent);

        await harness.SweepAsync();
        Assert.IsEmpty(harness.Dispatcher.Jobs);

        harness.Clock.Advance(HealthOptions.OfflineAfter);
        await harness.SweepAsync();

        Assert.HasCount(1, harness.JobsFor(manager.Id));
    }

    [TestMethod]
    public async Task Offline_WaitsForOpeningTimeAndThenAlerts()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        await SetOpeningHoursAsync(harness, new TimeOnly(16, 0), new TimeOnly(22, 0));
        await SeedKioskAsync(harness, Now - Silent);

        await harness.SweepAsync();
        Assert.IsEmpty(harness.Dispatcher.Jobs, "14:00 at the venue is before opening");

        harness.Clock.Advance(TimeSpan.FromHours(2));
        await harness.SweepAsync();

        Assert.HasCount(1, harness.JobsFor(manager.Id));
    }

    [TestMethod]
    public async Task BackOnline_FollowsAnOfflineAlertWithHowLongItWasDown()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        Client kiosk = await SeedKioskAsync(harness, Now - Silent);
        await harness.SweepAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        harness.Connected.Add(ConnectionId);
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();
        await harness.SweepAsync();

        List<NotificationJob> jobs = harness.JobsFor(manager.Id);
        Assert.HasCount(2, jobs);
        KioskBackOnlinePayload back = (KioskBackOnlinePayload)jobs[1].Payload;
        Assert.AreEqual(NotificationTopic.KioskOffline, jobs[1].Topic);
        Assert.AreEqual(38, back.DownForMinutes, "13:52:30 to 14:30");
    }

    [TestMethod]
    public async Task BackOnline_IsNotSentWhenNoOfflineAlertWent()
    {
        // Briefly unresponsive is not "offline", so there's nothing to take back.
        Harness harness = await BuildAsync();
        await SeedPersonAsync(harness, "Mia", "Manager");
        Client kiosk = await SeedKioskAsync(harness, Now - TimeSpan.FromMinutes(2));
        await harness.SweepAsync();

        harness.Connected.Add(ConnectionId);
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();

        Assert.IsEmpty(harness.Dispatcher.Jobs);
    }

    [TestMethod]
    public async Task CleanShutdown_AlertsOnlyOnceItHasLastedAsLongAsAnOutage()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        await SeedKioskAsync(harness, Now - TimeSpan.FromMinutes(2), lastDisconnect: Now - TimeSpan.FromMinutes(1));

        await harness.SweepAsync();
        Assert.IsEmpty(harness.Dispatcher.Jobs, "a restart or update reconnects within a few minutes");

        harness.Clock.Advance(HealthOptions.OfflineAfter);
        await harness.SweepAsync();

        KioskOfflinePayload payload = (KioskOfflinePayload)harness.JobsFor(manager.Id).Single().Payload;
        Assert.IsTrue(payload.ShutDownCleanly);
        Assert.AreEqual("13:59", payload.DownSince);
    }

    [TestMethod]
    public async Task NeedsAttention_WaitsTenMinutesAndOnlyRepeatsForANewProblem()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        harness.Connected.Add(ConnectionId);
        Client kiosk = await SeedKioskAsync(harness, Now, dmxBroken: true);

        await harness.SweepAsync();
        Assert.IsEmpty(harness.Dispatcher.Jobs);

        harness.Clock.Advance(HealthOptions.NeedsAttentionAfter);
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();

        NotificationJob first = harness.JobsFor(manager.Id).Single();
        Assert.AreEqual(NotificationTopic.KioskNeedsAttention, first.Topic);
        CollectionAssert.AreEqual(new[] { "DMX device not open" }, ((KioskNeedsAttentionPayload)first.Payload).Reasons);

        // The same problem again: nothing new to say.
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();
        Assert.HasCount(1, harness.JobsFor(manager.Id));

        // High CPU is new; its live number must not make it look new on every heartbeat after.
        await HeartbeatAsync(harness, kiosk.Id, x => x.CpuPercent = 95);
        await harness.SweepAsync();
        await HeartbeatAsync(harness, kiosk.Id, x => x.CpuPercent = 97);
        await harness.SweepAsync();

        List<NotificationJob> jobs = harness.JobsFor(manager.Id);
        Assert.HasCount(2, jobs);
        CollectionAssert.AreEqual(new[] { "DMX device not open", "High CPU (95%)" }, ((KioskNeedsAttentionPayload)jobs[1].Payload).Reasons);
    }

    [TestMethod]
    public async Task NeedsAttention_StartsAgainOnceTheKioskIsHealthy()
    {
        Harness harness = await BuildAsync();
        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        harness.Connected.Add(ConnectionId);
        Client kiosk = await SeedKioskAsync(harness, Now, dmxBroken: true);

        await harness.SweepAsync();
        harness.Clock.Advance(HealthOptions.NeedsAttentionAfter);
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();

        await HeartbeatAsync(harness, kiosk.Id, x => x.DmxConfigured = false);
        await harness.SweepAsync();

        await HeartbeatAsync(harness, kiosk.Id, x => x.DmxConfigured = true);
        await harness.SweepAsync();
        Assert.HasCount(1, harness.JobsFor(manager.Id), "a fresh problem waits its own ten minutes");

        harness.Clock.Advance(HealthOptions.NeedsAttentionAfter);
        await HeartbeatAsync(harness, kiosk.Id);
        await harness.SweepAsync();
        Assert.HasCount(2, harness.JobsFor(manager.Id));
    }

    [TestMethod]
    public async Task Recipients_AreTheLocationsManagersAndTheCompanysAdmins()
    {
        Harness harness = await BuildAsync();
        Location otherSite;

        await using (ApplicationDbContext ctx = new(harness.Options))
        {
            otherSite = new Location { CompanyId = harness.Company.Id, Name = "Colchester", IsActive = true };
            ctx.Locations.Add(otherSite);
            await ctx.SaveChangesAsync();
        }

        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(harness.Options, "Other Co", "Norwich");

        UserProfile manager = await SeedPersonAsync(harness, "Mia", "Manager");
        UserProfile headOfficeAdmin = await SeedPersonAsync(harness, "Ada", "Admin", otherSite);
        UserProfile otherSiteManager = await SeedPersonAsync(harness, "Olly", "Manager", otherSite);
        UserProfile otherCompanyAdmin = await SeedPersonAsync(harness, "Zed", "Admin", elsewhere);
        UserProfile lockedManager = await SeedPersonAsync(harness, "Lou", "Manager", lockedUntil: Now.AddDays(1));
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(harness.Options, harness.Location, "Sam");

        await SeedKioskAsync(harness, Now - Silent);
        await harness.SweepAsync();

        CollectionAssert.AreEquivalent(new[] { manager.Id, headOfficeAdmin.Id }, harness.Dispatcher.Jobs.Select(x => x.UserId).ToList());
        Assert.IsEmpty(harness.JobsFor(otherSiteManager.Id));
        Assert.IsEmpty(harness.JobsFor(otherCompanyAdmin.Id));
        Assert.IsEmpty(harness.JobsFor(lockedManager.Id));
        Assert.IsEmpty(harness.JobsFor(staff.Id));
    }

    [TestMethod]
    public async Task Recipients_AreOnlyAdminsForAKioskWithNoLocation()
    {
        Harness harness = await BuildAsync();
        await SeedPersonAsync(harness, "Mia", "Manager");
        UserProfile admin = await SeedPersonAsync(harness, "Ada", "Admin");
        await SetOpeningHoursAsync(harness, new TimeOnly(16, 0), new TimeOnly(22, 0));

        await SeedKioskAsync(harness, Now - Silent, withLocation: false);
        await harness.SweepAsync();

        NotificationJob job = harness.Dispatcher.Jobs.Single();
        Assert.AreEqual(admin.Id, job.UserId);
        Assert.AreEqual(0, job.Payload.LocationId);
        Assert.IsNull(((KioskOfflinePayload)job.Payload).LocationName, "and with no location's hours to wait for, it alerts straight away");
    }

    [TestMethod]
    public async Task DemoCompanyKiosks_NeverAlert()
    {
        Harness harness = await BuildAsync();
        await SeedPersonAsync(harness, "Ada", "Admin");

        await using (ApplicationDbContext ctx = new(harness.Options))
        {
            Company company = await ctx.Companies.SingleAsync(x => x.Id == harness.Company.Id);
            company.IsDemo = true;
            await ctx.SaveChangesAsync();
        }

        await SeedKioskAsync(harness, Now - Silent);
        await harness.SweepAsync();

        Assert.IsEmpty(harness.Dispatcher.Jobs);
    }

    [TestMethod]
    public void ReasonKey_DropsTheLiveNumbers()
    {
        Assert.AreEqual("High CPU", KioskAlertMonitorHostedService.ReasonKey("High CPU (95%)"));
        Assert.AreEqual("Low disk space", KioskAlertMonitorHostedService.ReasonKey("Low disk space (3.2 GB free)"));
        Assert.AreEqual("DMX device not open", KioskAlertMonitorHostedService.ReasonKey("DMX device not open"));
    }
}
