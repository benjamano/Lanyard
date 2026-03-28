#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Application.Services;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation
{
    [TestClass]
    public class AutomationRuleServiceTests
    {
        public TestContext TestContext { get; set; } = null!;

        private DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        private AutomationRuleService GetService(DbContextOptions<ApplicationDbContext> options)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(options));

            Mock<AutomationEngineService> engineMock = new Mock<AutomationEngineService>(MockBehavior.Loose);

            return new AutomationRuleService(factoryMock.Object, engineMock.Object);
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task CreateRuleAsync_ShouldAddRuleToDatabase()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task CreateRuleAsync_ShouldInvalidateRuleCache()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task GetRuleAsync_ShouldReturnRule()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task GetRuleAsync_ShouldReturnNullWhenNotFound()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task GetRulesByTriggerAsync_ShouldReturnMatchingRules()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task GetRulesByTriggerAsync_ShouldNotReturnInactiveRules()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task UpdateRuleAsync_ShouldUpdateRule()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task UpdateRuleAsync_ShouldInvalidateRuleCache()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task DeleteRuleAsync_ShouldSoftDeleteRule()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }

        [TestMethod]
        [Ignore("Wave 0 stub — implement after AutomationRuleService is complete")]
        public async Task DeleteRuleAsync_ShouldReturnFailureWhenNotFound()
        {
            Assert.Inconclusive("Not yet implemented");
            await Task.CompletedTask;
        }
    }
}
