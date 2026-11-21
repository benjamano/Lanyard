#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

/// <summary>
/// "Run now (test)" through the real container: the interface and the concrete engine must be the
/// same singleton (the hosted services use one, the app the other), and a test run must reach the
/// real executors and log a test row.
/// </summary>
[TestClass]
public class AutomationTestRunIntegrationTests
{
    [TestMethod]
    public async Task TestRun_ReachesTheRealMusicExecutor_AndLogsATestRow()
    {
        using CustomWebApplicationFactory factory = new();
        factory.CreateClient();

        IAutomationEngineService engine = factory.Services.GetRequiredService<IAutomationEngineService>();
        Assert.AreSame(factory.Services.GetRequiredService<AutomationEngineService>(), engine, "One engine, registered under both types.");

        ISystemDbContextFactory contextFactory = factory.Services.GetRequiredService<ISystemDbContextFactory>();
        Guid kioskId = Guid.NewGuid();
        Guid ruleId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = await contextFactory.CreateDbContextAsync())
        {
            // Never connected, so the real executor gets as far as "Client not connected".
            ctx.Clients.Add(new Client { Id = kioskId, CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, Name = "Arena 1", CreateDate = DateTime.UtcNow });
            ctx.AutomationRules.Add(new AutomationRule
            {
                Id = ruleId,
                CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
                Name = "Music on when a game starts",
                TriggerClientId = kioskId,
                TriggerEvent = GameStatus.InGame,
                IsActive = true,
                IsEnabled = true,
                CreateDate = DateTime.UtcNow,
                Actions =
                [
                    new AutomationRuleAction
                    {
                        Id = Guid.NewGuid(),
                        ActionType = AutomationActionTypes.MusicControl,
                        ParametersJson = $"{{\"TargetClientId\":\"{kioskId}\",\"Operation\":\"Play\"}}",
                        IsActive = true
                    }
                ]
            });
            await ctx.SaveChangesAsync();
        }

        Result<AutomationTestRunResult> result = await engine.TestRunRuleAsync(
            new AutomationTestRunRequest(ruleId, ApplicationDbContext.SeedPlay2DayCompanyId, "integration-user", "Integration Tester", CheckConditions: false));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Client not connected", result.Data!.Actions.Single().ErrorMessage);

        await using ApplicationDbContext check = await contextFactory.CreateDbContextAsync();
        AutomationRuleExecution execution = await check.AutomationRuleExecutions.Include(e => e.ActionExecutions).SingleAsync(e => e.AutomationRuleId == ruleId);

        Assert.IsTrue(execution.IsTestRun);
        Assert.AreEqual("integration-user", execution.TestRunByUserId);
        Assert.AreEqual("Manual test by Integration Tester", execution.TriggerEvent);
        Assert.IsFalse(execution.OverallSuccess);
    }
}
