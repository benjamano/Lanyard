using Lanyard.Application.Services;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.GameResults;

[TestClass]
public class GameResultServiceTests
{
    private const int Play2DayCompanyId = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int OtherCompanyId = 2;

    // A signed-in caller, so the company query filter applies (see CompanyTenancyTests).
    private sealed class SignedInTenant(int companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static GameResultService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new GameResultService(factoryMock.Object, new SystemDbContextFactory(options), NewCache(), TimeProvider.System, NullLogger<GameResultService>.Instance);
    }

    private static HallOfFameCache NewCache()
    {
        return new HallOfFameCache(new MemoryCache(new MemoryCacheOptions()), NullLogger<HallOfFameCache>.Instance);
    }

    private static GameResultService GetSignedInService(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options, new SignedInTenant(companyId)));

        Mock<ITenantContext> tenant = new();
        tenant.SetupGet(x => x.IsSystem).Returns(false);
        tenant.SetupGet(x => x.CompanyId).Returns(companyId);

        return new GameResultService(factoryMock.Object, new SystemDbContextFactory(options), NewCache(), TimeProvider.System, NullLogger<GameResultService>.Instance, tenant.Object);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new() { Id = Guid.NewGuid(), Name = $"Kiosk in company {companyId}", CompanyId = companyId };

        ctx.Clients.Add(client);

        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static async Task SeedCompanyGameAsync(DbContextOptions<ApplicationDbContext> options, int companyId, int gunId, int score, Team team)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.GameResults.Add(new GameResult
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            ClientId = Guid.NewGuid(),
            PlayedAtUtc = DateTime.UtcNow,
            DurationSeconds = 600,
            PlayerScores = [new GameResultPlayerScore { Id = Guid.NewGuid(), GunId = gunId, Score = score, Accuracy = 50, Team = team }]
        });

        await ctx.SaveChangesAsync();
    }

    private static LaserGameStatusDTO BuildFinishedGame(params (int GunId, int Score, int Accuracy, Team? Team)[] players)
    {
        return new LaserGameStatusDTO
        {
            Status = GameStatus.NotStarted,
            TotalTimeSeconds = 600,
            PlayerCount = players.Length,
            PlayerScores = [.. players.Select(p => new PlayerScoreDTO
            {
                GunId = p.GunId,
                Score = p.Score,
                Accuracy = p.Accuracy,
                Team = p.Team
            })]
        };
    }

    private static async Task SeedGameAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid clientId,
        DateTime playedAtUtc,
        params (int GunId, int Score, int Accuracy, Team? Team)[] players)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.GameResults.Add(new GameResult
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            PlayedAtUtc = playedAtUtc,
            DurationSeconds = 600,
            PlayerScores = [.. players.Select(p => new GameResultPlayerScore
            {
                Id = Guid.NewGuid(),
                GunId = p.GunId,
                Score = p.Score,
                Accuracy = p.Accuracy,
                Team = p.Team
            })]
        });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_PersistsGameAndPlayerScores()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = await SeedClientAsync(options, Play2DayCompanyId);

        Result<bool> result = await service.RecordCompletedGameAsync(
            clientId, BuildFinishedGame((3, 1200, 42, Team.Red), (4, 900, 30, Team.Green)));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(result.Data);

        await using ApplicationDbContext ctx = new(options);

        GameResult game = await ctx.GameResults.Include(x => x.PlayerScores).SingleAsync();

        Assert.AreEqual(clientId, game.ClientId);
        Assert.AreEqual(600, game.DurationSeconds);
        Assert.AreEqual(2, game.PlayerScores.Count);

        GameResultPlayerScore topScore = game.PlayerScores.Single(x => x.GunId == 3);

        Assert.AreEqual(1200, topScore.Score);
        Assert.AreEqual(42, topScore.Accuracy);
        Assert.AreEqual(Team.Red, topScore.Team);

        // Nothing populates a player name anywhere in the pipeline yet.
        Assert.IsNull(topScore.PlayerName);
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_SkipsGameWithNoPlayers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);

        // No Client row either: an empty game is skipped before the client is looked up, so it
        // is Ok(false) rather than an unknown-client failure.
        Result<bool> result = await service.RecordCompletedGameAsync(Guid.NewGuid(), BuildFinishedGame());

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Data, "A game with no players should not be recorded.");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.GameResults.CountAsync());
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_StoresGameUnderTheClientsCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = await SeedClientAsync(options, OtherCompanyId);

        Result<bool> result = await service.RecordCompletedGameAsync(clientId, BuildFinishedGame((1, 500, 40, Team.Red)));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(result.Data);

        await using ApplicationDbContext ctx = new(options);
        GameResult game = await ctx.GameResults.SingleAsync();

        // A system context (the kiosk hub) would otherwise default this to Play2Day.
        Assert.AreEqual(OtherCompanyId, game.CompanyId);
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_KeepsPlay2DayClientsGamesInPlay2Day()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = await SeedClientAsync(options, Play2DayCompanyId);

        Result<bool> result = await service.RecordCompletedGameAsync(clientId, BuildFinishedGame((1, 500, 40, Team.Red)));

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(Play2DayCompanyId, (await ctx.GameResults.SingleAsync()).CompanyId);
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_FailsForUnknownClient()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);

        Result<bool> result = await service.RecordCompletedGameAsync(Guid.NewGuid(), BuildFinishedGame((1, 500, 40, Team.Red)));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "client was not found");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.GameResults.CountAsync());
    }

    [TestMethod]
    public async Task GameResultService_RecordCompletedGame_SignedInCallerCannotRecordForAnotherCompanysClient()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid otherCompanysClientId = await SeedClientAsync(options, OtherCompanyId);
        GameResultService service = GetSignedInService(options, Play2DayCompanyId);

        Result<bool> result = await service.RecordCompletedGameAsync(otherCompanysClientId, BuildFinishedGame((1, 500, 40, Team.Red)));

        Assert.IsFalse(result.Success, "The company filter hides the other company's client, so the game must not be written.");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.GameResults.CountAsync());
    }

    [TestMethod]
    public async Task GameResultService_GetTopScores_WithCompanyId_OnlyReturnsThatCompanysGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        // System context, as on an anonymous kiosk display - no tenant filter applies.
        GameResultService service = GetService(options);

        await SeedCompanyGameAsync(options, Play2DayCompanyId, gunId: 1, score: 9000, Team.Red);
        await SeedCompanyGameAsync(options, OtherCompanyId, gunId: 2, score: 500, Team.Green);

        Result<IReadOnlyList<HallOfFameEntryDTO>> scoped =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, null, OtherCompanyId);

        Assert.IsTrue(scoped.Success, scoped.Error);
        Assert.AreEqual(1, scoped.Data!.Count);
        Assert.AreEqual(2, scoped.Data[0].GunId);

        Result<IReadOnlyList<HallOfFameEntryDTO>> accuracy =
            await service.GetTopAccuracyAsync(HallOfFamePeriod.AllTime, 5, null, OtherCompanyId);

        Assert.IsTrue(accuracy.Success, accuracy.Error);
        Assert.AreEqual(1, accuracy.Data!.Count);

        Result<IReadOnlyList<HallOfFameEntryDTO>> unscoped =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, null, null);

        Assert.IsTrue(unscoped.Success, unscoped.Error);
        Assert.AreEqual(2, unscoped.Data!.Count);
    }

    [TestMethod]
    public async Task GameResultService_GetTopTeamTotals_WithCompanyId_OnlyCountsThatCompanysGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);

        await SeedCompanyGameAsync(options, Play2DayCompanyId, gunId: 1, score: 9000, Team.Red);
        await SeedCompanyGameAsync(options, OtherCompanyId, gunId: 2, score: 500, Team.Red);

        Result<IReadOnlyList<HallOfFameTeamTotalDTO>> result =
            await service.GetTopTeamTotalsAsync(HallOfFamePeriod.AllTime, null, OtherCompanyId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(500, result.Data[0].TotalScore);
        Assert.AreEqual(1, result.Data[0].GameCount);
    }

    [TestMethod]
    public async Task GameResultService_GetTopScores_SignedInCallerOnlySeesTheirOwnCompanysGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedCompanyGameAsync(options, Play2DayCompanyId, gunId: 1, score: 9000, Team.Red);
        await SeedCompanyGameAsync(options, OtherCompanyId, gunId: 2, score: 500, Team.Green);

        GameResultService service = GetSignedInService(options, OtherCompanyId);

        Result<IReadOnlyList<HallOfFameEntryDTO>> result =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, null, null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(2, result.Data[0].GunId);
    }

    [TestMethod]
    public async Task GameResultService_GetTopScores_OrdersByScoreDescendingAndRespectsTake()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = Guid.NewGuid();

        await SeedGameAsync(options, clientId, DateTime.UtcNow,
            (1, 500, 10, Team.Red), (2, 1500, 20, Team.Green), (3, 1000, 30, Team.Red));

        Result<IReadOnlyList<HallOfFameEntryDTO>> result =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 2, null, null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data!.Count);
        Assert.AreEqual(1500, result.Data[0].Score);
        Assert.AreEqual(1000, result.Data[1].Score);
    }

    [TestMethod]
    public async Task GameResultService_GetTopAccuracy_OrdersByAccuracyNotScore()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = Guid.NewGuid();

        await SeedGameAsync(options, clientId, DateTime.UtcNow,
            (1, 5000, 12, Team.Red), (2, 100, 95, Team.Green));

        Result<IReadOnlyList<HallOfFameEntryDTO>> result =
            await service.GetTopAccuracyAsync(HallOfFamePeriod.AllTime, 5, null, null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(95, result.Data![0].Accuracy);
        Assert.AreEqual(2, result.Data[0].GunId);
    }

    [TestMethod]
    public async Task GameResultService_GetTopScores_FiltersByClient()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);

        Guid wantedClientId = Guid.NewGuid();
        Guid otherClientId = Guid.NewGuid();

        await SeedGameAsync(options, wantedClientId, DateTime.UtcNow, (1, 500, 10, Team.Red));
        await SeedGameAsync(options, otherClientId, DateTime.UtcNow, (2, 9999, 99, Team.Green));

        Result<IReadOnlyList<HallOfFameEntryDTO>> scoped =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, wantedClientId, null);

        Assert.IsTrue(scoped.Success, scoped.Error);
        Assert.AreEqual(1, scoped.Data!.Count);
        Assert.AreEqual(500, scoped.Data[0].Score);

        Result<IReadOnlyList<HallOfFameEntryDTO>> venueWide =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, null, null);

        Assert.IsTrue(venueWide.Success, venueWide.Error);
        Assert.AreEqual(2, venueWide.Data!.Count);
    }

    [TestMethod]
    public async Task GameResultService_GetTopScores_ExcludesGamesBeforeThePeriodStarts()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = Guid.NewGuid();

        // Local-midnight boundary, expressed in UTC the same way the service resolves it.
        DateTime todayStartUtc = HallOfFamePeriod.Today.ToUtcLowerBound(DateTime.Now)!.Value;

        await SeedGameAsync(options, clientId, todayStartUtc.AddMinutes(-1), (1, 9999, 99, Team.Red));
        await SeedGameAsync(options, clientId, todayStartUtc.AddMinutes(1), (2, 100, 10, Team.Green));

        Result<IReadOnlyList<HallOfFameEntryDTO>> today =
            await service.GetTopScoresAsync(HallOfFamePeriod.Today, 5, null, null);

        Assert.IsTrue(today.Success, today.Error);
        Assert.AreEqual(1, today.Data!.Count);
        Assert.AreEqual(2, today.Data[0].GunId, "Yesterday's game should not appear in Today.");

        Result<IReadOnlyList<HallOfFameEntryDTO>> allTime =
            await service.GetTopScoresAsync(HallOfFamePeriod.AllTime, 5, null, null);

        Assert.IsTrue(allTime.Success, allTime.Error);
        Assert.AreEqual(2, allTime.Data!.Count);
    }

    [TestMethod]
    public async Task GameResultService_GetTopTeamTotals_SumsScoresPerTeamAndCountsGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = Guid.NewGuid();

        await SeedGameAsync(options, clientId, DateTime.UtcNow, (1, 100, 10, Team.Red), (2, 50, 10, Team.Green));
        await SeedGameAsync(options, clientId, DateTime.UtcNow, (1, 300, 10, Team.Red), (2, 20, 10, Team.Green));

        Result<IReadOnlyList<HallOfFameTeamTotalDTO>> result =
            await service.GetTopTeamTotalsAsync(HallOfFamePeriod.AllTime, null, null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data!.Count);

        HallOfFameTeamTotalDTO red = result.Data.Single(x => x.Team == Team.Red);

        Assert.AreEqual(400, red.TotalScore);
        Assert.AreEqual(2, red.GameCount);

        // Highest total first, so the widget can render the list as-is.
        Assert.AreEqual(Team.Red, result.Data[0].Team);
    }

    [TestMethod]
    public async Task GameResultService_GetTopTeamTotals_IgnoresPlayersWithNoTeam()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        GameResultService service = GetService(options);
        Guid clientId = Guid.NewGuid();

        await SeedGameAsync(options, clientId, DateTime.UtcNow, (1, 100, 10, Team.Red), (2, 999, 10, null));

        Result<IReadOnlyList<HallOfFameTeamTotalDTO>> result =
            await service.GetTopTeamTotalsAsync(HallOfFamePeriod.AllTime, null, null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(100, result.Data[0].TotalScore);
    }
}
