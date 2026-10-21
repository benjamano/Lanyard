using Lanyard.Application.Services;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.GameResults;

/// <summary>
/// The Hall of Fame widget reads through a process-wide cache shared by every circuit. These pin
/// down what makes that safe: one load per window until a game is recorded, a company only ever
/// sees its own rows, and failed or superseded loads are never served.
/// </summary>
[TestClass]
public class HallOfFameCacheTests
{
    private const int CompanyA = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int CompanyB = 2;

    private sealed class Harness(GameResultService service, Mock<ISystemDbContextFactory> systemFactory, HallOfFameCache cache)
    {
        public GameResultService Service { get; } = service;
        public HallOfFameCache Cache { get; } = cache;

        // Every snapshot load opens exactly one system context, so this counts real loads.
        public int Loads => systemFactory.Invocations.Count(x => x.Method.Name == nameof(ISystemDbContextFactory.CreateDbContextAsync));
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static HallOfFameCache NewCache()
    {
        return new HallOfFameCache(new MemoryCache(new MemoryCacheOptions()), NullLogger<HallOfFameCache>.Instance);
    }

    // signedInCompanyId null = system caller (a kiosk display); otherwise a signed-in member of
    // that company. A cache can be passed in so two callers share it, as circuits do.
    private static Harness GetHarness(
        DbContextOptions<ApplicationDbContext> options,
        HallOfFameCache? cache = null,
        TimeProvider? clock = null,
        int? signedInCompanyId = null,
        bool signedInWithoutCompany = false)
    {
        cache ??= NewCache();

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISystemDbContextFactory> systemFactory = new();
        systemFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        ITenantContext? tenant = null;

        if (signedInCompanyId.HasValue || signedInWithoutCompany)
        {
            Mock<ITenantContext> tenantMock = new();
            tenantMock.SetupGet(x => x.IsSystem).Returns(false);
            tenantMock.SetupGet(x => x.CompanyId).Returns(signedInCompanyId);
            tenant = tenantMock.Object;
        }

        GameResultService service = new(factory.Object, systemFactory.Object, cache, clock ?? TimeProvider.System, NullLogger<GameResultService>.Instance, tenant);

        return new Harness(service, systemFactory, cache);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new() { Id = Guid.NewGuid(), Name = $"Kiosk in company {companyId}", CompanyId = companyId };
        ctx.Clients.Add(client);

        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static async Task SeedGameAsync(DbContextOptions<ApplicationDbContext> options, int companyId, Guid clientId, DateTime playedAtUtc, int gunId, int score, int accuracy, Team team)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.GameResults.Add(new GameResult
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            ClientId = clientId,
            PlayedAtUtc = playedAtUtc,
            DurationSeconds = 600,
            PlayerScores = [new GameResultPlayerScore { Id = Guid.NewGuid(), GunId = gunId, Score = score, Accuracy = accuracy, Team = team }]
        });

        await ctx.SaveChangesAsync();
    }

    private static LaserGameStatusDTO BuildFinishedGame(int gunId, int score)
    {
        return new LaserGameStatusDTO
        {
            Status = GameStatus.NotStarted,
            TotalTimeSeconds = 600,
            PlayerCount = 1,
            PlayerScores = [new PlayerScoreDTO { GunId = gunId, Score = score, Accuracy = 50, Team = Team.Red }]
        };
    }

    [TestMethod]
    public async Task HallOfFame_RepeatedRead_IsServedFromCacheWithoutQuerying()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);
        await SeedGameAsync(options, CompanyA, Guid.NewGuid(), DateTime.UtcNow, 1, 900, 40, Team.Red);

        Result<HallOfFameSnapshotDTO> first = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);
        Result<HallOfFameSnapshotDTO> second = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);

        Assert.IsTrue(first.Success, first.Error);
        Assert.IsTrue(second.Success, second.Error);
        Assert.AreEqual(1, harness.Loads, "The second read should come from the cache.");
        Assert.AreEqual(900, second.Data!.TopScores[0].Score);
    }

    [TestMethod]
    public async Task HallOfFame_Snapshot_MatchesTheIndividualQueries()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);
        Guid clientId = Guid.NewGuid();

        await SeedGameAsync(options, CompanyA, clientId, DateTime.UtcNow, 1, 500, 90, Team.Red);
        await SeedGameAsync(options, CompanyA, clientId, DateTime.UtcNow, 2, 1500, 20, Team.Green);
        await SeedGameAsync(options, CompanyA, clientId, DateTime.UtcNow, 3, 1000, 55, Team.Red);
        await SeedGameAsync(options, CompanyA, clientId, DateTime.UtcNow, 4, 700, 55, Team.Green);

        HallOfFameSnapshotDTO snapshot = (await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA)).Data!;

        IReadOnlyList<HallOfFameEntryDTO> scores = (await harness.Service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA)).Data!;
        IReadOnlyList<HallOfFameEntryDTO> accuracy = (await harness.Service.GetTopAccuracyAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA)).Data!;
        IReadOnlyList<HallOfFameTeamTotalDTO> teams = (await harness.Service.GetTopTeamTotalsAsync(HallOfFamePeriod.AllTime, null, CompanyA)).Data!;

        CollectionAssert.AreEqual(scores.Select(x => x.GunId).ToList(), snapshot.TopScores.Select(x => x.GunId).ToList());
        CollectionAssert.AreEqual(accuracy.Select(x => x.GunId).ToList(), snapshot.TopAccuracy.Select(x => x.GunId).ToList());
        CollectionAssert.AreEqual(teams.Select(x => (x.Team, x.TotalScore, x.GameCount)).ToList(), snapshot.TeamTotals.Select(x => (x.Team, x.TotalScore, x.GameCount)).ToList());
    }

    [TestMethod]
    public async Task HallOfFame_RecordingAGame_InvalidatesAndRaisesChangedForThatCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);
        Guid clientId = await SeedClientAsync(options, CompanyB);

        Result<HallOfFameSnapshotDTO> before = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);
        Assert.AreEqual(0, before.Data!.TopScores.Count);

        List<int> changed = [];
        int? scoreSeenFromHandler = null;

        // A widget reloads from inside the handler, so the cache must already be clear by then.
        harness.Cache.Changed += companyId =>
        {
            changed.Add(companyId);
            scoreSeenFromHandler = harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB).Result.Data!.TopScores.FirstOrDefault()?.Score;
        };

        Result<bool> recorded = await harness.Service.RecordCompletedGameAsync(clientId, BuildFinishedGame(gunId: 7, score: 4321));

        Assert.IsTrue(recorded.Success, recorded.Error);
        CollectionAssert.AreEqual(new List<int> { CompanyB }, changed);
        Assert.AreEqual(4321, scoreSeenFromHandler, "The handler's reload should see the new game, not the cached empty board.");

        Result<HallOfFameSnapshotDTO> after = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);
        Assert.AreEqual(4321, after.Data!.TopScores[0].Score);
    }

    [TestMethod]
    public async Task HallOfFame_GamesThatAreNotRecorded_DoNotRaiseChanged()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);

        int raised = 0;
        harness.Cache.Changed += _ => raised++;

        // No players: skipped. Unknown client: failed. Neither wrote anything.
        await harness.Service.RecordCompletedGameAsync(Guid.NewGuid(), new LaserGameStatusDTO { Status = GameStatus.NotStarted });
        await harness.Service.RecordCompletedGameAsync(Guid.NewGuid(), BuildFinishedGame(1, 100));

        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public async Task HallOfFame_InvalidatingOneCompany_KeepsOtherCompaniesCached()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);

        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);
        Assert.AreEqual(2, harness.Loads);

        harness.Cache.Invalidate(CompanyA);

        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);

        Assert.AreEqual(3, harness.Loads, "Only company A should have been reloaded.");
    }

    [TestMethod]
    public async Task HallOfFame_DifferentClientPeriodOrSize_AreCachedSeparately()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);
        Guid clientId = Guid.NewGuid();

        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, clientId, CompanyA);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.ThisMonth, 3, null, CompanyA);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 5, null, CompanyA);

        Assert.AreEqual(4, harness.Loads);
    }

    [TestMethod]
    public async Task HallOfFame_SignedInCaller_CannotReadAnotherCompanysCachedBoard()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        HallOfFameCache sharedCache = NewCache();
        await SeedGameAsync(options, CompanyB, Guid.NewGuid(), DateTime.UtcNow, 2, 9999, 99, Team.Green);

        // A kiosk display for company B warms B's entry.
        Harness kiosk = GetHarness(options, sharedCache);
        Result<HallOfFameSnapshotDTO> kioskView = await kiosk.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);
        Assert.AreEqual(1, kioskView.Data!.TopScores.Count);

        // A company A member asking for B gets nothing - not B's cached rows.
        Harness companyAUser = GetHarness(options, sharedCache, signedInCompanyId: CompanyA);
        Result<HallOfFameSnapshotDTO> crossTenant = await companyAUser.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyB);

        Assert.IsTrue(crossTenant.Success, crossTenant.Error);
        Assert.AreEqual(0, crossTenant.Data!.TopScores.Count);
        Assert.AreEqual(0, crossTenant.Data.TeamTotals.Count);
        Assert.AreEqual(0, companyAUser.Loads, "A mismatched company must not touch the cache or the database.");

        // With no company (the editor), they get their own company's board, not B's.
        Result<HallOfFameSnapshotDTO> ownBoard = await companyAUser.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, null);
        Assert.AreEqual(0, ownBoard.Data!.TopScores.Count);
    }

    [TestMethod]
    public async Task HallOfFame_SignedInCallerAndKioskForTheSameCompany_ShareOneEntry()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        HallOfFameCache sharedCache = NewCache();
        await SeedGameAsync(options, CompanyA, Guid.NewGuid(), DateTime.UtcNow, 1, 800, 40, Team.Red);

        Harness kiosk = GetHarness(options, sharedCache);
        Harness editor = GetHarness(options, sharedCache, signedInCompanyId: CompanyA);

        Result<HallOfFameSnapshotDTO> kioskView = await kiosk.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);
        Result<HallOfFameSnapshotDTO> editorView = await editor.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, null);

        Assert.AreEqual(1, kiosk.Loads);
        Assert.AreEqual(0, editor.Loads, "The editor's read should hit the kiosk's entry.");
        Assert.AreEqual(kioskView.Data!.TopScores[0].Score, editorView.Data!.TopScores[0].Score);
    }

    [TestMethod]
    public async Task HallOfFame_SignedInCallerWithNoCompany_GetsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedGameAsync(options, CompanyA, Guid.NewGuid(), DateTime.UtcNow, 1, 800, 40, Team.Red);

        Harness harness = GetHarness(options, signedInWithoutCompany: true);

        Result<HallOfFameSnapshotDTO> result = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, CompanyA);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(0, result.Data!.TopScores.Count);
        Assert.AreEqual(0, harness.Loads);
    }

    [TestMethod]
    public async Task HallOfFame_SystemCallerWithNoCompany_IsNeverCached()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Harness harness = GetHarness(options);
        await SeedGameAsync(options, CompanyA, Guid.NewGuid(), DateTime.UtcNow, 1, 800, 40, Team.Red);
        await SeedGameAsync(options, CompanyB, Guid.NewGuid(), DateTime.UtcNow, 2, 700, 40, Team.Red);

        Result<HallOfFameSnapshotDTO> first = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, null);
        await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.AllTime, 3, null, null);

        Assert.AreEqual(2, first.Data!.TopScores.Count);
        Assert.AreEqual(2, harness.Loads);
    }

    [TestMethod]
    public async Task HallOfFame_TodayRollsOverAtMidnightWithoutAGameBeingRecorded()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DateTime localNoonUtc = DateTime.SpecifyKind(DateTime.Today.AddHours(12), DateTimeKind.Local).ToUniversalTime();
        TestClock clock = new(localNoonUtc);
        Harness harness = GetHarness(options, clock: clock);

        await SeedGameAsync(options, CompanyA, Guid.NewGuid(), localNoonUtc.AddHours(-1), 1, 800, 40, Team.Red);

        Result<HallOfFameSnapshotDTO> today = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.Today, 3, null, CompanyA);
        Assert.AreEqual(1, today.Data!.TopScores.Count);

        clock.Advance(TimeSpan.FromDays(1));

        Result<HallOfFameSnapshotDTO> tomorrow = await harness.Service.GetHallOfFameAsync(HallOfFamePeriod.Today, 3, null, CompanyA);
        Assert.AreEqual(0, tomorrow.Data!.TopScores.Count, "Yesterday's cached board must not be served as today's.");
    }

    [TestMethod]
    public async Task HallOfFameCache_FailedLoad_IsNotCached()
    {
        HallOfFameCache cache = NewCache();
        int calls = 0;

        Task<HallOfFameSnapshotDTO> Load()
        {
            calls++;
            return calls == 1
                ? Task.FromException<HallOfFameSnapshotDTO>(new InvalidOperationException("database down"))
                : Task.FromResult(new HallOfFameSnapshotDTO());
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.GetOrLoadAsync(CompanyA, null, null, 3, Load));

        HallOfFameSnapshotDTO result = await cache.GetOrLoadAsync(CompanyA, null, null, 3, Load);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, calls, "The retry should run a fresh load.");
    }

    [TestMethod]
    public async Task HallOfFameCache_ConcurrentMisses_ShareOneLoad()
    {
        HallOfFameCache cache = NewCache();
        TaskCompletionSource<HallOfFameSnapshotDTO> pending = new();
        int calls = 0;

        Task<HallOfFameSnapshotDTO> Load()
        {
            Interlocked.Increment(ref calls);
            return pending.Task;
        }

        // Ten widgets reloading together after a game ends.
        Task<HallOfFameSnapshotDTO>[] readers = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => cache.GetOrLoadAsync(CompanyA, null, null, 3, Load)))
            .ToArray();

        await Task.Delay(50);
        pending.SetResult(new HallOfFameSnapshotDTO());
        HallOfFameSnapshotDTO[] results = await Task.WhenAll(readers);

        Assert.AreEqual(1, calls);
        Assert.IsTrue(results.All(x => ReferenceEquals(x, results[0])));
    }

    [TestMethod]
    public async Task HallOfFameCache_LoadInFlightWhenInvalidated_IsNotKept()
    {
        HallOfFameCache cache = NewCache();
        TaskCompletionSource<HallOfFameSnapshotDTO> stale = new();
        int calls = 0;

        Task<HallOfFameSnapshotDTO> Load()
        {
            calls++;
            return calls == 1 ? stale.Task : Task.FromResult(new HallOfFameSnapshotDTO());
        }

        // A load that read the database before the game was saved...
        Task<HallOfFameSnapshotDTO> inFlight = cache.GetOrLoadAsync(CompanyA, null, null, 3, Load);

        // ...then the game is recorded while it's still running.
        cache.Invalidate(CompanyA);
        stale.SetResult(new HallOfFameSnapshotDTO());
        await inFlight;

        await cache.GetOrLoadAsync(CompanyA, null, null, 3, Load);

        Assert.AreEqual(2, calls, "The pre-invalidation result must not be served afterwards.");
    }
}
