#nullable enable

using Lanyard.Application.Services;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Lanyard.Tests.Services.OperationsAlerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Automation;

// A rule whose action fails is raised on the Operations alerts page, and cleared when it next runs
// cleanly. Skipped rules aren't failures.
[TestClass]
public class AutomationEngineAlertTests
{
    private sealed record Setup(
        DbContextOptions<ApplicationDbContext> Options,
        AutomationEngineService Engine,
        RecordingOperationsAlertWriter Alerts,
        AutomationRule Rule,
        Guid ClientId);

    private static async Task<Setup> BuildAsync(Func<AutomationRuleAction, (bool, string?)> behaviour, int? kioskLocationId = 7)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Guid clientId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "AutomationEngine.Enabled", Value = "true", CreateDate = DateTime.UtcNow });
            ctx.Clients.Add(new Client { Id = clientId, CompanyId = 4, Name = "Arena PC", LocationId = kioskLocationId });
            ctx.AutomationRules.Add(new AutomationRule
            {
                Id = Guid.NewGuid(),
                CompanyId = 4,
                Name = "Lights up",
                TriggerClientId = clientId,
                TriggerEvent = GameStatus.InGame,
                IsActive = true,
                IsEnabled = true,
                CreateDate = DateTime.UtcNow,
                Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", IsActive = true }]
            });
            await ctx.SaveChangesAsync();
        }

        AutomationRule rule;
        await using (ApplicationDbContext ctx = new(options))
        {
            rule = await ctx.AutomationRules.SingleAsync();
        }

        RecordingOperationsAlertWriter alerts = new();
        AutomationEngineService engine = new(new SystemDbContextFactory(options), [new RecordingActionExecutor(behaviour)],
            new LaserGameStatusStore(), alerts, TimeProvider.System, NullLogger<AutomationEngineService>.Instance);

        return new Setup(options, engine, alerts, rule, clientId);
    }

    private static Task FireAsync(Setup setup, GameStatus from = GameStatus.NotStarted, GameStatus to = GameStatus.InGame) =>
        setup.Engine.ProcessTransitionAsync(new GameStatusTransitionEvent(setup.ClientId, from, to), CancellationToken.None);

    [TestMethod]
    public async Task AFailedAction_RaisesOneAlertForTheRuleAtTheKiosksLocation()
    {
        Setup setup = await BuildAsync(_ => (false, "Kiosk didn't answer"));

        await FireAsync(setup);

        OperationsAlertRaise raised = setup.Alerts.Raised.Single();
        Assert.AreEqual($"automation-failed:{setup.Rule.Id:N}", raised.DedupKey);
        Assert.AreEqual(OperationsAlertType.AutomationRuleFailed, raised.Type);
        Assert.AreEqual(OperationsAlertSeverity.Warning, raised.Severity);
        Assert.AreEqual(4, raised.CompanyId);
        Assert.AreEqual(7, raised.LocationId);
        Assert.AreEqual(setup.Rule.Id, raised.SubjectRuleId);
        Assert.AreEqual(NotificationTopic.AutomationRuleFailed, raised.NotifyTopic);
        Assert.Contains("Lights up", raised.Title);
        Assert.Contains("Kiosk didn't answer", raised.Detail!);
        Assert.IsEmpty(setup.Alerts.Resolved);
    }

    [TestMethod]
    public async Task AKioskWithNoLocation_RaisesACompanyWideAlert()
    {
        Setup setup = await BuildAsync(_ => (false, "boom"), kioskLocationId: null);

        await FireAsync(setup);

        Assert.IsNull(setup.Alerts.Raised.Single().LocationId);
    }

    [TestMethod]
    public async Task ACleanRun_ClearsTheRulesAlert()
    {
        bool fail = true;
        Setup setup = await BuildAsync(_ => fail ? (false, "boom") : (true, null));

        await FireAsync(setup);
        fail = false;
        await FireAsync(setup, GameStatus.InGame, GameStatus.NotStarted);
        await FireAsync(setup);

        Assert.HasCount(1, setup.Alerts.Raised);
        Assert.AreEqual($"automation-failed:{setup.Rule.Id:N}", setup.Alerts.Resolved.Single().DedupKey);
    }

    [TestMethod]
    public async Task AnAlertStoreThatThrows_DoesNotStopTheExecutionLog()
    {
        Setup setup = await BuildAsync(_ => (false, "boom"));
        setup.Alerts.Fail = true;

        await FireAsync(setup);

        await using ApplicationDbContext ctx = new(setup.Options);
        Assert.IsFalse((await ctx.AutomationRuleExecutions.SingleAsync()).OverallSuccess);
    }
}
