using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.GameResults;

// The game history list on the Laser game status widget: GetRecentGamesAsync, the winner rules
// in ToRecentGame, and the start time RecordCompletedGameAsync now stores.
[TestClass]
public class GameResultServiceRecentGamesTests
{
    private const int CompanyA = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int CompanyB = 2;

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

        return new GameResultService(factoryMock.Object, NullLogger<GameResultService>.Instance);
    }

    private static GameResultPlayerScore Player(int gunId, int score, Team? team) => new()
    {
        Id = Guid.NewGuid(),
        GunId = gunId,
        Score = score,
        Accuracy = 50,
        Team = team
    };

    private static GameResult Game(Guid clientId, DateTime playedAtUtc, int companyId = CompanyA, DateTime? startedAtUtc = null, params GameResultPlayerScore[] players) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = companyId,
        ClientId = clientId,
        PlayedAtUtc = playedAtUtc,
        StartedAtUtc = startedAtUtc,
        DurationSeconds = 600,
        PlayerScores = [.. players]
    };

    private static async Task SeedAsync(DbContextOptions<ApplicationDbContext> options, params GameResult[] games)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.GameResults.AddRange(games);
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task GetRecentGamesAsync_ReturnsThisKiosksGames_NewestFirst_UpToTake()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid kiosk = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        await SeedAsync(options,
            Game(kiosk, now.AddHours(-3), players: Player(1, 10, Team.Red)),
            Game(kiosk, now.AddHours(-1), players: Player(1, 30, Team.Red)),
            Game(kiosk, now.AddHours(-2), players: Player(1, 20, Team.Red)),
            Game(Guid.NewGuid(), now, players: Player(1, 99, Team.Red)));

        Result<IReadOnlyList<RecentGameDTO>> result = await GetService(options).GetRecentGamesAsync(kiosk, 2, null);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { 30, 20 }, result.Data!.Select(x => x.WinningScore).ToArray());
    }

    [TestMethod]
    public async Task GetRecentGamesAsync_LeavesOutOtherCompanies_WhenACompanyIsGiven()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid kiosk = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        await SeedAsync(options,
            Game(kiosk, now, CompanyA, players: Player(1, 10, Team.Red)),
            Game(kiosk, now.AddMinutes(-5), CompanyB, players: Player(1, 20, Team.Red)));

        Result<IReadOnlyList<RecentGameDTO>> result = await GetService(options).GetRecentGamesAsync(kiosk, 10, CompanyA);

        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(10, result.Data[0].WinningScore);
    }

    [TestMethod]
    public void ToRecentGame_HighestTeamTotalWins_WithBothTeamTotalsAndPlayerCount()
    {
        GameResult game = Game(Guid.NewGuid(), DateTime.UtcNow, players:
        [
            Player(1, 300, Team.Red),
            Player(2, 300, Team.Red),
            Player(3, 500, Team.Green),
        ]);

        RecentGameDTO dto = GameResultService.ToRecentGame(game);

        Assert.AreEqual(Team.Red, dto.WinningTeam);
        Assert.AreEqual(600, dto.WinningScore);
        Assert.IsFalse(dto.IsDraw);
        Assert.AreEqual(3, dto.PlayerCount);
        CollectionAssert.AreEqual(new[] { Team.Red, Team.Green }, dto.TeamTotals.Select(x => x.Team).ToArray());
        CollectionAssert.AreEqual(new[] { 600, 500 }, dto.TeamTotals.Select(x => x.TotalScore).ToArray());
    }

    [TestMethod]
    public void ToRecentGame_EqualTeamTotals_IsADraw()
    {
        GameResult game = Game(Guid.NewGuid(), DateTime.UtcNow, players: [Player(1, 400, Team.Red), Player(2, 400, Team.Green)]);

        RecentGameDTO dto = GameResultService.ToRecentGame(game);

        Assert.IsTrue(dto.IsDraw);
        Assert.IsNull(dto.WinningTeam);
        Assert.AreEqual(400, dto.WinningScore);
    }

    [TestMethod]
    public void ToRecentGame_NoTeams_TopGunWins()
    {
        GameResult game = Game(Guid.NewGuid(), DateTime.UtcNow, players: [Player(4, 100, null), Player(7, 900, null)]);

        RecentGameDTO dto = GameResultService.ToRecentGame(game);

        Assert.IsNull(dto.WinningTeam);
        Assert.AreEqual(7, dto.WinningGunId);
        Assert.AreEqual(900, dto.WinningScore);
        Assert.AreEqual(0, dto.TeamTotals.Count);
        Assert.IsFalse(dto.IsDraw);
    }

    [TestMethod]
    public void ToRecentGame_NoTeams_TiedTopGuns_IsADraw()
    {
        GameResult game = Game(Guid.NewGuid(), DateTime.UtcNow, players: [Player(4, 900, null), Player(7, 900, null)]);

        RecentGameDTO dto = GameResultService.ToRecentGame(game);

        Assert.IsTrue(dto.IsDraw);
        Assert.IsNull(dto.WinningGunId);
    }

    [TestMethod]
    public void ToRecentGame_UsesTheRecordedStart_WhenThereIsOne()
    {
        DateTime ended = new(2026, 11, 28, 15, 0, 0, DateTimeKind.Utc);
        DateTime started = ended.AddMinutes(-4);

        RecentGameDTO dto = GameResultService.ToRecentGame(Game(Guid.NewGuid(), ended, startedAtUtc: started, players: Player(1, 1, Team.Red)));

        Assert.AreEqual(started, dto.StartedAtUtc);
        Assert.IsFalse(dto.StartIsEstimate);
        Assert.AreEqual(ended, dto.EndedAtUtc);
    }

    [TestMethod]
    public void ToRecentGame_EstimatesTheStartFromTheGameLength_ForOlderGames()
    {
        DateTime ended = new(2026, 11, 28, 15, 0, 0, DateTimeKind.Utc);

        RecentGameDTO dto = GameResultService.ToRecentGame(Game(Guid.NewGuid(), ended, players: Player(1, 1, Team.Red)));

        Assert.AreEqual(ended.AddSeconds(-600), dto.StartedAtUtc);
        Assert.IsTrue(dto.StartIsEstimate);
    }

    [TestMethod]
    public async Task RecordCompletedGameAsync_StoresTheStartTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId;
        await using (ApplicationDbContext ctx = new(options))
        {
            Client client = new() { Id = Guid.NewGuid(), Name = "Arena kiosk", CompanyId = CompanyA };
            ctx.Clients.Add(client);
            await ctx.SaveChangesAsync();
            clientId = client.Id;
        }

        DateTime started = DateTime.UtcNow.AddMinutes(-10);
        LaserGameStatusDTO status = new()
        {
            Status = GameStatus.NotStarted,
            TotalTimeSeconds = 600,
            PlayerScores = [new PlayerScoreDTO { GunId = 1, Score = 100, Accuracy = 20, Team = Team.Red }]
        };

        Result<bool> result = await GetService(options).RecordCompletedGameAsync(clientId, status, started);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext verify = new(options);
        Assert.AreEqual(started, (await verify.GameResults.SingleAsync()).StartedAtUtc);
    }

    [TestMethod]
    public async Task RecordCompletedGameAsync_DropsAStartAfterTheEnd()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId;
        await using (ApplicationDbContext ctx = new(options))
        {
            Client client = new() { Id = Guid.NewGuid(), Name = "Arena kiosk", CompanyId = CompanyA };
            ctx.Clients.Add(client);
            await ctx.SaveChangesAsync();
            clientId = client.Id;
        }

        LaserGameStatusDTO status = new()
        {
            Status = GameStatus.NotStarted,
            TotalTimeSeconds = 600,
            PlayerScores = [new PlayerScoreDTO { GunId = 1, Score = 100, Accuracy = 20, Team = Team.Red }]
        };

        await GetService(options).RecordCompletedGameAsync(clientId, status, DateTime.UtcNow.AddHours(1));

        await using ApplicationDbContext verify = new(options);
        Assert.IsNull((await verify.GameResults.SingleAsync()).StartedAtUtc);
    }
}
