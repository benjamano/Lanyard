#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation
{
    [TestClass]
    public class AutomationLogServiceTests
    {
        public TestContext TestContext { get; set; } = null!;

        private DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        private AutomationLogService GetService(DbContextOptions<ApplicationDbContext> options)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock =
                new Mock<IDbContextFactory<ApplicationDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(options));
            return new AutomationLogService(factoryMock.Object);
        }

        [TestMethod]
        public async Task GetRecentExecutionsAsync_ReturnsOrderedByExecutedAtDesc()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            await using (ApplicationDbContext ctx = new ApplicationDbContext(options))
            {
                AutomationRule rule = new AutomationRule
                {
                    Id = Guid.NewGuid(),
                    Name = "TestRule",
                    IsActive = true,
                    CreateDate = DateTime.UtcNow
                };
                ctx.AutomationRules.Add(rule);

                ctx.AutomationRuleExecutions.AddRange(
                    new AutomationRuleExecution
                    {
                        Id = Guid.NewGuid(),
                        AutomationRuleId = rule.Id,
                        RuleName = "TestRule",
                        ExecutedAt = DateTime.UtcNow.AddMinutes(-10),
                        TriggerEvent = "InGame",
                        OverallSuccess = true
                    },
                    new AutomationRuleExecution
                    {
                        Id = Guid.NewGuid(),
                        AutomationRuleId = rule.Id,
                        RuleName = "TestRule",
                        ExecutedAt = DateTime.UtcNow.AddMinutes(-1),
                        TriggerEvent = "NotStarted",
                        OverallSuccess = false
                    }
                );
                await ctx.SaveChangesAsync();
            }

            AutomationLogService service = GetService(options);
            Result<IEnumerable<AutomationRuleExecution>> result =
                await service.GetRecentExecutionsAsync(50);

            Assert.IsTrue(result.IsSuccess);
            List<AutomationRuleExecution> list = result.Data!.ToList();
            Assert.AreEqual(2, list.Count);
            Assert.IsTrue(list[0].ExecutedAt > list[1].ExecutedAt,
                "First entry should be more recent (desc order)");
        }

        [TestMethod]
        public async Task GetRecentExecutionsAsync_RespectsCountLimit()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            await using (ApplicationDbContext ctx = new ApplicationDbContext(options))
            {
                AutomationRule rule = new AutomationRule
                {
                    Id = Guid.NewGuid(),
                    Name = "TestRule",
                    IsActive = true,
                    CreateDate = DateTime.UtcNow
                };
                ctx.AutomationRules.Add(rule);
                for (int i = 0; i < 5; i++)
                {
                    ctx.AutomationRuleExecutions.Add(new AutomationRuleExecution
                    {
                        Id = Guid.NewGuid(),
                        AutomationRuleId = rule.Id,
                        RuleName = "TestRule",
                        ExecutedAt = DateTime.UtcNow.AddMinutes(-i),
                        TriggerEvent = "InGame",
                        OverallSuccess = true
                    });
                }
                await ctx.SaveChangesAsync();
            }

            AutomationLogService service = GetService(options);
            Result<IEnumerable<AutomationRuleExecution>> result =
                await service.GetRecentExecutionsAsync(3);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(3, result.Data!.Count());
        }

        [TestMethod]
        public async Task GetLatestExecutionForRule_IgnoresManualTests_ButTheLogShowsThem()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            Guid ruleId = Guid.NewGuid();
            Guid realRunId = Guid.NewGuid();

            await using (ApplicationDbContext ctx = new ApplicationDbContext(options))
            {
                ctx.AutomationRules.Add(new AutomationRule { Id = ruleId, Name = "TestRule", IsActive = true, CreateDate = DateTime.UtcNow });
                ctx.AutomationRuleExecutions.AddRange(
                    new AutomationRuleExecution { Id = realRunId, AutomationRuleId = ruleId, RuleName = "TestRule", ExecutedAt = DateTime.UtcNow.AddHours(-2), TriggerEvent = "InGame", OverallSuccess = false },
                    new AutomationRuleExecution { Id = Guid.NewGuid(), AutomationRuleId = ruleId, RuleName = "TestRule", ExecutedAt = DateTime.UtcNow, TriggerEvent = "Manual test by Sam", OverallSuccess = true, IsTestRun = true, TestRunByUserId = "u1" });
                await ctx.SaveChangesAsync();
            }

            AutomationLogService service = GetService(options);

            Result<AutomationRuleExecution?> latest = await service.GetLatestExecutionForRuleAsync(ruleId);
            Result<IEnumerable<AutomationRuleExecution>> recent = await service.GetRecentExecutionsAsync(50);

            Assert.AreEqual(realRunId, latest.Data!.Id, "A passing test mustn't hide that the rule last failed for real.");
            Assert.AreEqual(2, recent.Data!.Count());
            Assert.IsTrue(recent.Data!.First().IsTestRun);
        }
    }
}
