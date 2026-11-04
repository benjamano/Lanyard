using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection.Bindings;

[TestClass]
public class GameBindingSourceTests
{
    private const int CompanyId = 1;
    private const int OtherCompanyId = 2;
    private const int ArenaLocationId = 10;
    private const int OtherLocationId = 11;

    // 12:00 UTC in July is 13:00 in London, so venue-local midnight is 23:00 UTC the day before.
    private static readonly DateTime NowUtc = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ISystemDbContextFactory GetFactory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    private static ProjectionBindingContext Context(int companyId = CompanyId, int? locationId = null) => new(companyId, locationId, Guid.NewGuid(), NowUtc);

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, int companyId = CompanyId, int? locationId = null)
    {
        Guid clientId = Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);
        ctx.Clients.Add(new Client { Id = clientId, CompanyId = companyId, Name = "Laser system", LocationId = locationId });
        await ctx.SaveChangesAsync();

        return clientId;
    }

    private static async Task SeedGameAsync(DbContextOptions<ApplicationDbContext> options, Guid clientId, DateTime playedAtUtc, int companyId = CompanyId, params (int Gun, int Score, Team? Team)[] scores)
    {
        await using ApplicationDbContext ctx = new(options);

        Guid gameId = Guid.NewGuid();

        ctx.GameResults.Add(new GameResult
        {
            Id = gameId,
            CompanyId = companyId,
            ClientId = clientId,
            PlayedAtUtc = playedAtUtc,
            DurationSeconds = 600,
            PlayerScores = [.. scores.Select(s => new GameResultPlayerScore { Id = Guid.NewGuid(), GameResultId = gameId, GunId = s.Gun, Score = s.Score, Team = s.Team })]
        });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task LastWinner_NamesTheWinningTeamOfTheMostRecentGame()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        await SeedGameAsync(options, clientId, NowUtc.AddHours(-3), CompanyId, (1, 9000, Team.Red), (2, 100, Team.Green));
        await SeedGameAsync(options, clientId, NowUtc.AddHours(-1), CompanyId, (1, 3000, Team.Red), (2, 4000, Team.Green), (3, 1500, Team.Red));

        Result<string?> result = await new LastGameWinnerBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Red team won with 4,500", result.Data);
    }

    [TestMethod]
    public async Task LastWinner_SaysItsADraw_WhenTheTopTeamsTie()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        await SeedGameAsync(options, clientId, NowUtc.AddHours(-1), CompanyId, (1, 2000, Team.Red), (2, 2000, Team.Green));

        Result<string?> result = await new LastGameWinnerBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.AreEqual("It's a draw!", result.Data);
    }

    [TestMethod]
    public async Task LastWinner_NamesTheTopGun_InASoloGame()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        await SeedGameAsync(options, clientId, NowUtc.AddHours(-1), CompanyId, (4, 1200, null), (7, 4520, null));

        Result<string?> result = await new LastGameWinnerBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.AreEqual("Gun 7 won with 4,520", result.Data);
    }

    [TestMethod]
    public async Task LastWinner_HasNoData_WhenTheOnlyGameWasBeforeVenueMidnight()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = await SeedClientAsync(options);

        // 22:30 UTC on the 14th is 23:30 in London on the 14th: yesterday, even though UTC-wise
        // it's within 24 hours.
        await SeedGameAsync(options, clientId, new DateTime(2026, 7, 14, 22, 30, 0, DateTimeKind.Utc), CompanyId, (1, 5000, null));

        Result<string?> result = await new LastGameWinnerBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task LastWinner_IgnoresAnotherCompanysGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid otherClientId = await SeedClientAsync(options, OtherCompanyId);

        await SeedGameAsync(options, otherClientId, NowUtc.AddMinutes(-5), OtherCompanyId, (1, 9999, null));

        Result<string?> result = await new LastGameWinnerBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task TopScore_IsTheBestScoreToday_AtThisLocationOnly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid arenaClientId = await SeedClientAsync(options, locationId: ArenaLocationId);
        Guid otherVenueClientId = await SeedClientAsync(options, locationId: OtherLocationId);

        await SeedGameAsync(options, arenaClientId, NowUtc.AddHours(-2), CompanyId, (3, 3100, null), (5, 2800, null));
        await SeedGameAsync(options, otherVenueClientId, NowUtc.AddHours(-1), CompanyId, (9, 8000, null));

        TopScoreTodayBindingSource source = new(GetFactory(options));

        Result<string?> atArena = await source.ResolveAsync(Context(locationId: ArenaLocationId), CancellationToken.None);
        Result<string?> companyWide = await source.ResolveAsync(Context(locationId: null), CancellationToken.None);

        Assert.AreEqual("Today's top score: Gun 3 – 3,100", atArena.Data);
        Assert.AreEqual("Today's top score: Gun 9 – 8,000", companyWide.Data);
    }

    [TestMethod]
    public async Task TopScore_CountsALaserSystemWithNoLocation_AsCompanyWide()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid unassignedClientId = await SeedClientAsync(options, locationId: null);

        await SeedGameAsync(options, unassignedClientId, NowUtc.AddHours(-1), CompanyId, (2, 1500, null));

        Result<string?> result = await new TopScoreTodayBindingSource(GetFactory(options)).ResolveAsync(Context(locationId: ArenaLocationId), CancellationToken.None);

        Assert.AreEqual("Today's top score: Gun 2 – 1,500", result.Data);
    }

    [TestMethod]
    public async Task TopScore_HasNoData_WithNoGamesToday_OrOnlyAnotherCompanysGames()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid otherClientId = await SeedClientAsync(options, OtherCompanyId);

        await SeedGameAsync(options, otherClientId, NowUtc.AddHours(-1), OtherCompanyId, (1, 7000, null));

        Result<string?> result = await new TopScoreTodayBindingSource(GetFactory(options)).ResolveAsync(Context(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public void PlayerLabel_PrefersAPlayerName_WhenOneIsRecorded()
    {
        GameResultPlayerScore named = new() { GunId = 4, PlayerName = "  Sam  ", Score = 10 };
        GameResultPlayerScore unnamed = new() { GunId = 4, Score = 10 };

        Assert.AreEqual("Sam won with 10", LastGameWinnerBindingSource.DescribeWinner([named]));
        Assert.AreEqual("Gun 4 won with 10", LastGameWinnerBindingSource.DescribeWinner([unnamed]));
    }
}
