using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskHealthServiceTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private sealed class TestableKioskHealthService(
        IDbContextFactory<ApplicationDbContext> factory,
        TimeProvider clock,
        IReadOnlyCollection<string> connectedIds)
        : KioskHealthService(factory, Microsoft.Extensions.Options.Options.Create(new KioskHealthOptions()), clock, NullLogger<KioskHealthService>.Instance)
    {
        protected override IReadOnlyCollection<string> GetConnectedConnectionIds() => connectedIds;
    }

    private static TestableKioskHealthService GetService(DbContextOptions<ApplicationDbContext> options, TestClock clock, params string[] connectedIds)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new TestableKioskHealthService(factoryMock.Object, clock, connectedIds);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, string? connectionId = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new() { Id = Guid.NewGuid(), Name = "Arena kiosk", MostRecentConnectionId = connectionId };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static KioskHealthReportDTO Report(Action<KioskHealthReportDTO>? configure = null)
    {
        KioskHealthReportDTO report = new()
        {
            ClientVersion = "1.0.41",
            ProcessStartedUtc = Start.AddHours(-1),
            MachineUptimeSeconds = 7200,
            CpuPercent = 12,
            MemoryPercent = 50,
            SystemDriveFreeBytes = 100L * 1024 * 1024 * 1024,
            SystemDriveTotalBytes = 250L * 1024 * 1024 * 1024
        };

        configure?.Invoke(report);

        return report;
    }

    [TestMethod]
    public async Task RecordReport_CreatesTheSnapshot_OnTheFirstBeat()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);
        TestClock clock = new(Start);

        Result<bool> result = await GetService(options, clock).RecordReportAsync(clientId, Report());

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Data);

        await using ApplicationDbContext ctx = new(options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync();
        Assert.AreEqual(clientId, snapshot.ClientId);
        Assert.AreEqual(Start, snapshot.FirstHeartbeatUtc);
        Assert.AreEqual(Start, snapshot.LastHeartbeatUtc);
        Assert.AreEqual("1.0.41", snapshot.ClientVersion);
    }

    [TestMethod]
    public async Task RecordReport_OverwritesTheSnapshot_AndKeepsTheFirstBeat()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock);

        await service.RecordReportAsync(clientId, Report());
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.RecordReportAsync(clientId, Report(r => r.CpuPercent = 77));

        await using ApplicationDbContext ctx = new(options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync();
        Assert.AreEqual(Start, snapshot.FirstHeartbeatUtc);
        Assert.AreEqual(Start.AddSeconds(30), snapshot.LastHeartbeatUtc);
        Assert.AreEqual(77, snapshot.CpuPercent);
    }

    [TestMethod]
    public async Task RecordReport_IgnoresReportsThatComeTooQuickly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock);

        await service.RecordReportAsync(clientId, Report());
        clock.Advance(TimeSpan.FromSeconds(2));
        Result<bool> second = await service.RecordReportAsync(clientId, Report(r => r.CpuPercent = 99));

        Assert.IsTrue(second.IsSuccess);
        Assert.IsFalse(second.Data);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(12, (await ctx.ClientHealthSnapshots.SingleAsync()).CpuPercent);
    }

    [TestMethod]
    public async Task RecordReport_ClampsAndValidatesWhatTheKioskSends()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        await GetService(options, new TestClock(Start)).RecordReportAsync(clientId, Report(r =>
        {
            r.CpuPercent = 250;
            r.MemoryPercent = double.NaN;
            r.SystemDriveFreeBytes = -5;
            r.ClientVersion = "1.0.41; DROP TABLE";
            r.ProjectionRunningCount = -3;
            r.MachineUptimeSeconds = -1;
        }));

        await using ApplicationDbContext ctx = new(options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync();
        Assert.AreEqual(100, snapshot.CpuPercent);
        Assert.IsNull(snapshot.MemoryPercent);
        Assert.IsNull(snapshot.SystemDriveFreeBytes);
        Assert.IsNull(snapshot.ClientVersion);
        Assert.AreEqual(0, snapshot.ProjectionRunningCount);
        Assert.AreEqual(0, snapshot.MachineUptimeSeconds);
    }

    [TestMethod]
    public async Task RecordReport_FailsForAnUnknownClient()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<bool> result = await GetService(options, new TestClock(Start)).RecordReportAsync(Guid.NewGuid(), Report());

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetHealth_CoversEveryKiosk_IncludingOnesThatNeverReported()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid reporting = await SeedClientAsync(options, "conn-1");
        Guid legacy = await SeedClientAsync(options, "conn-2");
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock, "conn-1", "conn-2");

        await service.RecordReportAsync(reporting, Report());

        Result<Dictionary<Guid, KioskHealthView>> health = await service.GetHealthAsync();

        Assert.IsTrue(health.IsSuccess);
        Assert.AreEqual(KioskHealthStatus.Healthy, health.Data![reporting].Status);
        Assert.AreEqual(KioskHealthStatus.NoTelemetry, health.Data[legacy].Status);
    }

    [TestMethod]
    public async Task GetHealth_FlagsAKioskThatStoppedBeating_WhileStillConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options, "conn-1");
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock, "conn-1");

        await service.RecordReportAsync(clientId, Report());
        clock.Advance(TimeSpan.FromMinutes(2));

        Result<Dictionary<Guid, KioskHealthView>> health = await service.GetHealthAsync();

        Assert.AreEqual(KioskHealthStatus.Unresponsive, health.Data![clientId].Status);
    }

    [TestMethod]
    public async Task GetHealth_ForGivenKiosks_MatchesTheFullLoad()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid reporting = await SeedClientAsync(options, "conn-1");
        Guid legacy = await SeedClientAsync(options, "conn-2");
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock, "conn-1", "conn-2");

        await service.RecordReportAsync(reporting, Report());

        Result<Dictionary<Guid, KioskHealthView>> full = await service.GetHealthAsync();
        Result<Dictionary<Guid, KioskHealthView>> given = await service.GetHealthAsync(
        [
            new KioskHealthClient(reporting, true, null),
            new KioskHealthClient(legacy, true, null)
        ]);

        Assert.IsTrue(given.IsSuccess);
        CollectionAssert.AreEquivalent(full.Data!.Keys, given.Data!.Keys);

        foreach (Guid id in full.Data.Keys)
        {
            Assert.AreEqual(full.Data[id].Status, given.Data[id].Status);
            CollectionAssert.AreEqual(full.Data[id].Reasons.ToList(), given.Data[id].Reasons.ToList());
        }
    }

    [TestMethod]
    public async Task GetHealth_ForGivenKiosks_TakesTheConnectedFlagAsGiven()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options, "conn-1");
        TestClock clock = new(Start);

        // The hub set says connected; the caller's own snapshot of it says not.
        KioskHealthService service = GetService(options, clock, "conn-1");

        await service.RecordReportAsync(clientId, Report());
        clock.Advance(TimeSpan.FromSeconds(5));

        Result<Dictionary<Guid, KioskHealthView>> health = await service.GetHealthAsync(
            [new KioskHealthClient(clientId, false, Start.AddSeconds(2))]);

        Assert.AreEqual(KioskHealthStatus.Disconnected, health.Data![clientId].Status);
    }

    [TestMethod]
    public async Task GetHealth_ForGivenKiosks_StillComparesVersionsAcrossTheWholeCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid older = await SeedClientAsync(options, "conn-1");
        Guid newer = await SeedClientAsync(options, "conn-2");
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock, "conn-1", "conn-2");

        await service.RecordReportAsync(older, Report(x => x.ClientVersion = "1.0.41"));
        await service.RecordReportAsync(newer, Report(x => x.ClientVersion = "1.0.42"));

        Result<Dictionary<Guid, KioskHealthView>> health = await service.GetHealthAsync([new KioskHealthClient(older, true, null)]);

        Assert.AreEqual(1, health.Data!.Count, "Only the kiosks asked about are returned.");
        Assert.AreEqual("1.0.42", health.Data[older].NewerVersionSeen);
    }

    [TestMethod]
    public async Task GetClientHealth_ReturnsJustThatKiosk()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid older = await SeedClientAsync(options, "conn-1");
        Guid newer = await SeedClientAsync(options, "conn-2");
        Guid legacy = await SeedClientAsync(options, "conn-3");
        TestClock clock = new(Start);
        KioskHealthService service = GetService(options, clock, "conn-1", "conn-2", "conn-3");

        await service.RecordReportAsync(older, Report(x => x.ClientVersion = "1.0.41"));
        await service.RecordReportAsync(newer, Report(x => x.ClientVersion = "1.0.42"));

        Result<KioskHealthView?> olderHealth = await service.GetClientHealthAsync(older);
        Result<KioskHealthView?> legacyHealth = await service.GetClientHealthAsync(legacy);
        Result<KioskHealthView?> unknown = await service.GetClientHealthAsync(Guid.NewGuid());

        Assert.AreEqual(older, olderHealth.Data!.Snapshot!.ClientId);
        Assert.AreEqual("1.0.42", olderHealth.Data.NewerVersionSeen);
        Assert.AreEqual(KioskHealthStatus.NoTelemetry, legacyHealth.Data!.Status);
        Assert.IsTrue(unknown.IsSuccess);
        Assert.IsNull(unknown.Data);
    }

    [TestMethod]
    public async Task RecordReport_StoresTheOfflineFallbackFields()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);
        DateTime started = Start.AddHours(-3);

        await GetService(options, new TestClock(Start)).RecordReportAsync(clientId, Report(r =>
        {
            r.OfflineFallbackReady = false;
            r.OfflineVideosNotCached = 2;
            r.OfflineCacheBytes = 123_456;
            r.OfflineCacheSyncedUtc = Start.AddMinutes(-5);
            r.LastOfflineStartedUtc = started;
            r.LastOfflineEndedUtc = started.AddMinutes(12);
        }));

        await using ApplicationDbContext ctx = new(options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync();
        Assert.IsFalse(snapshot.OfflineFallbackReady);
        Assert.AreEqual(2, snapshot.OfflineVideosNotCached);
        Assert.AreEqual(123_456, snapshot.OfflineCacheBytes);
        Assert.AreEqual(Start.AddMinutes(-5), snapshot.OfflineCacheSyncedUtc);
        Assert.AreEqual(started, snapshot.LastOfflineStartedUtc);
        Assert.AreEqual(started.AddMinutes(12), snapshot.LastOfflineEndedUtc);
    }

    [TestMethod]
    public async Task RecordReport_FromAnOlderClient_LeavesTheOfflineFieldsEmpty()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        await GetService(options, new TestClock(Start)).RecordReportAsync(clientId, Report(r => r.OfflineVideosNotCached = -4));

        await using ApplicationDbContext ctx = new(options);
        ClientHealthSnapshot snapshot = await ctx.ClientHealthSnapshots.SingleAsync();
        Assert.IsNull(snapshot.OfflineFallbackReady);
        Assert.IsNull(snapshot.OfflineVideosNotCached, "A negative count is nonsense and isn't stored.");
        Assert.IsNull(snapshot.LastOfflineStartedUtc);
    }
}
