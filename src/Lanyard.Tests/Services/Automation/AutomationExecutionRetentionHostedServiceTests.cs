using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

// The purge uses ExecuteDeleteAsync, which the EF InMemory provider the other service tests rely
// on does not support - so this one runs against a real relational engine (SQLite in memory).
[TestClass]
public class AutomationExecutionRetentionHostedServiceTests
{
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
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _connection.DisposeAsync();
    }

    private AutomationExecutionRetentionHostedService GetService(int retentionDays)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        return new AutomationExecutionRetentionHostedService(
            factoryMock.Object,
            Options.Create(new AutomationExecutionLogOptions { RetentionDays = retentionDays }),
            NullLogger<AutomationExecutionRetentionHostedService>.Instance);
    }

    private async Task<(Guid oldExecutionId, Guid recentExecutionId)> SeedExecutionsAsync(DateTime now)
    {
        await using ApplicationDbContext ctx = new(_options);

        Client client = new() { Id = Guid.NewGuid(), Name = "Kiosk 1" };
        AutomationRule rule = new() { Id = Guid.NewGuid(), Name = "Rule", TriggerClientId = client.Id, TriggerEvent = GameStatus.InGame, IsActive = true, CreateDate = now };
        AutomationRuleAction action = new() { Id = Guid.NewGuid(), AutomationRuleId = rule.Id, ActionType = "Test", IsActive = true };

        AutomationRuleExecution old = new() { Id = Guid.NewGuid(), AutomationRuleId = rule.Id, RuleName = "Rule", ExecutedAt = now.AddDays(-120), OverallSuccess = true };
        AutomationRuleExecution recent = new() { Id = Guid.NewGuid(), AutomationRuleId = rule.Id, RuleName = "Rule", ExecutedAt = now.AddDays(-5), OverallSuccess = true };

        ctx.Clients.Add(client);
        ctx.AutomationRules.Add(rule);
        ctx.AutomationRuleActions.Add(action);
        ctx.AutomationRuleExecutions.AddRange(old, recent);
        ctx.AutomationRuleActionExecutions.AddRange(
            new AutomationRuleActionExecution { Id = Guid.NewGuid(), AutomationRuleExecutionId = old.Id, AutomationRuleActionId = action.Id, Success = true },
            new AutomationRuleActionExecution { Id = Guid.NewGuid(), AutomationRuleExecutionId = recent.Id, AutomationRuleActionId = action.Id, Success = true });

        await ctx.SaveChangesAsync();

        return (old.Id, recent.Id);
    }

    [TestMethod]
    public async Task PurgeAsync_DeletesExecutionsAndTheirActionsOlderThanRetention_KeepsRecentOnes()
    {
        DateTime now = DateTime.UtcNow;
        (Guid oldId, Guid recentId) = await SeedExecutionsAsync(now);

        int deleted = await GetService(retentionDays: 90).PurgeAsync(now, CancellationToken.None);

        Assert.AreEqual(1, deleted);

        await using ApplicationDbContext ctx = new(_options);
        Assert.IsNull(await ctx.AutomationRuleExecutions.FindAsync(oldId));
        Assert.IsNotNull(await ctx.AutomationRuleExecutions.FindAsync(recentId));
        Assert.AreEqual(0, await ctx.AutomationRuleActionExecutions.CountAsync(a => a.AutomationRuleExecutionId == oldId));
        Assert.AreEqual(1, await ctx.AutomationRuleActionExecutions.CountAsync(a => a.AutomationRuleExecutionId == recentId));
    }

    [TestMethod]
    public async Task PurgeAsync_IsDisabled_WhenRetentionIsZero()
    {
        DateTime now = DateTime.UtcNow;
        await SeedExecutionsAsync(now);

        int deleted = await GetService(retentionDays: 0).PurgeAsync(now, CancellationToken.None);

        Assert.AreEqual(0, deleted);

        await using ApplicationDbContext ctx = new(_options);
        Assert.AreEqual(2, await ctx.AutomationRuleExecutions.CountAsync());
    }
}
