using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

// The purge uses ExecuteDeleteAsync, which EF InMemory doesn't support, so this runs on SQLite.
[TestClass]
public class KioskSelfTestRetentionHostedServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using ApplicationDbContext ctx = new(_options);
        await ctx.Database.EnsureCreatedAsync();

        ctx.Companies.Add(new Company { Id = ApplicationDbContext.SeedPlay2DayCompanyId, Name = "Play2Day" });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _connection.DisposeAsync();
    }

    private KioskSelfTestRetentionHostedService GetService(int retentionDays)
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        return new KioskSelfTestRetentionHostedService(
            factory.Object,
            Options.Create(new KioskSelfTestOptions { RetentionDays = retentionDays }),
            NullLogger<KioskSelfTestRetentionHostedService>.Instance);
    }

    private async Task<(Guid OldRun, Guid RecentRun)> SeedAsync()
    {
        await using ApplicationDbContext ctx = new(_options);

        Client client = new() { Id = Guid.NewGuid(), Name = "Kiosk 1" };

        ClientSelfTestRun Run(int daysAgo) => new()
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            RequestedAtUtc = Now.AddDays(-daysAgo),
            Status = ClientSelfTestStatus.Completed,
            Checks = [new ClientSelfTestCheck { Id = Guid.NewGuid(), Key = SelfTestCheckKeys.AudioDevice, Outcome = SelfTestOutcome.Pass }]
        };

        ClientSelfTestRun old = Run(91);
        ClientSelfTestRun recent = Run(89);

        ctx.Clients.Add(client);
        ctx.ClientSelfTestRuns.AddRange(old, recent);
        await ctx.SaveChangesAsync();

        return (old.Id, recent.Id);
    }

    [TestMethod]
    public async Task PurgeAsync_RemovesRunsPastRetention_WithTheirChecks()
    {
        (Guid oldRun, Guid recentRun) = await SeedAsync();

        int deleted = await GetService(90).PurgeAsync(Now, CancellationToken.None);

        Assert.AreEqual(1, deleted);

        await using ApplicationDbContext ctx = new(_options);
        CollectionAssert.AreEqual(new[] { recentRun }, await ctx.ClientSelfTestRuns.Select(x => x.Id).ToArrayAsync());
        Assert.IsFalse(await ctx.ClientSelfTestChecks.AnyAsync(x => x.RunId == oldRun));
        Assert.AreEqual(1, await ctx.ClientSelfTestChecks.CountAsync());
    }

    [TestMethod]
    public async Task PurgeAsync_ZeroRetentionKeepsEverything()
    {
        await SeedAsync();

        Assert.AreEqual(0, await GetService(0).PurgeAsync(Now, CancellationToken.None));

        await using ApplicationDbContext ctx = new(_options);
        Assert.AreEqual(2, await ctx.ClientSelfTestRuns.CountAsync());
    }

    [TestMethod]
    public async Task DeletingAKiosk_DeletesItsSelfTests()
    {
        await SeedAsync();

        await using (ApplicationDbContext ctx = new(_options))
        {
            await ctx.Clients.ExecuteDeleteAsync();
        }

        await using ApplicationDbContext verify = new(_options);
        Assert.AreEqual(0, await verify.ClientSelfTestRuns.CountAsync());
        Assert.AreEqual(0, await verify.ClientSelfTestChecks.CountAsync());
    }
}
