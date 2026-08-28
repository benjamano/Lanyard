using Lanyard.Application.Services;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Json;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class StopProjectionProgramActionExecutorTests
{
    private sealed class TestableStopProjectionProgramActionExecutor(
        IServiceScopeFactory scopeFactory,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        IProjectionProgramRunnerService runnerService,
        ILogger<StopProjectionProgramActionExecutor> logger,
        bool isClientConnected) : StopProjectionProgramActionExecutor(scopeFactory, contextFactory, runnerService, logger)
    {
        protected override bool IsClientConnected(string connectionId) => isClientConnected;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    private static Mock<IServiceScopeFactory> GetScopeFactoryMock(IClientService clientService)
    {
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientService);

        Mock<IServiceScope> scopeMock = new();
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        return scopeFactoryMock;
    }

    private static StopProjectionProgramActionExecutor GetExecutor(
        DbContextOptions<ApplicationDbContext> options,
        IClientService clientService,
        IProjectionProgramRunnerService runnerService,
        bool isClientConnected)
    {
        return new TestableStopProjectionProgramActionExecutor(
            GetScopeFactoryMock(clientService).Object,
            GetFactoryMock(options).Object,
            runnerService,
            new Mock<ILogger<StopProjectionProgramActionExecutor>>().Object,
            isClientConnected);
    }

    private static async Task<Client> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, string? connectionId = "connection-1")
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Client",
            MostRecentConnectionId = connectionId,
            CreateDate = DateTime.UtcNow
        };

        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client;
    }

    private static AutomationRuleAction GetAction(Guid targetClientId, int? displayIndex)
    {
        return new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            ActionType = AutomationActionTypes.StopProjectionProgram,
            ParametersJson = JsonSerializer.Serialize(new { TargetClientId = targetClientId, DisplayIndex = displayIndex }),
            IsActive = true
        };
    }

    [TestMethod]
    public void CanHandle_OnlyHandlesStopProjectionProgram()
    {
        StopProjectionProgramActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), Mock.Of<IClientService>(), Mock.Of<IProjectionProgramRunnerService>(), true);

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.StopProjectionProgram));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.StartProjectionProgram));
    }

    [TestMethod]
    public async Task ExecuteAsync_StopsTheRunAndClosesTheKioskWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(x => x.Stop(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .Returns(Result<bool>.Ok(true));

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 2))
            .ReturnsAsync(Result<bool>.Ok(true));

        StopProjectionProgramActionExecutor executor = GetExecutor(options, clientServiceMock.Object, runnerMock.Object, true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(client.Id, 2), client.Id);

        Assert.IsTrue(success, error);

        // Both halves matter: Stop alone raises OnProgramStopped, which nothing listens to for
        // window teardown, so the last frame would stay frozen on the screen.
        runnerMock.Verify(x => x.Stop(client.Id, 2, null), Times.Once);
        clientServiceMock.Verify(x => x.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 2), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_DefaultsToDisplayZeroWhenNoDisplayConfigured()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(x => x.Stop(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .Returns(Result<bool>.Ok(true));

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 0))
            .ReturnsAsync(Result<bool>.Ok(true));

        StopProjectionProgramActionExecutor executor = GetExecutor(options, clientServiceMock.Object, runnerMock.Object, true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(client.Id, null), client.Id);

        Assert.IsTrue(success, error);
        runnerMock.Verify(x => x.Stop(client.Id, 0, null), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_FailsWhenClientIsNotConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        Mock<IClientService> clientServiceMock = new();

        StopProjectionProgramActionExecutor executor = GetExecutor(options, clientServiceMock.Object, runnerMock.Object, false);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(client.Id, 0), client.Id);

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
        runnerMock.Verify(x => x.Stop(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_FailsWhenNoClientConfigured()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        StopProjectionProgramActionExecutor executor = GetExecutor(
            options, Mock.Of<IClientService>(), Mock.Of<IProjectionProgramRunnerService>(), true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(Guid.Empty, 0), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a client", error);
    }
}
