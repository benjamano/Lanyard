using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class ProjectionProgramControlActionExecutorTests
{
    private sealed class TestableProjectionProgramControlActionExecutor(
        IServiceScopeFactory scopeFactory,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        IProjectionProgramRunnerService runnerService,
        ILogger<ProjectionProgramControlActionExecutor> logger,
        bool isClientConnected) : ProjectionProgramControlActionExecutor(scopeFactory, contextFactory, runnerService, logger)
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

    // Both Start (IProjectionProgramService) and Stop (IClientService) resolve a fresh scope, so
    // the mock needs to be able to serve either regardless of which operation a test exercises.
    private static Mock<IServiceScopeFactory> GetScopeFactoryMock(
        IProjectionProgramService? projectionProgramService = null,
        IClientService? clientService = null)
    {
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IProjectionProgramService)))
            .Returns(projectionProgramService ?? Mock.Of<IProjectionProgramService>());
        providerMock.Setup(p => p.GetService(typeof(IClientService)))
            .Returns(clientService ?? Mock.Of<IClientService>());

        Mock<IServiceScope> scopeMock = new();
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        return scopeFactoryMock;
    }

    private static ProjectionProgramControlActionExecutor GetExecutor(
        DbContextOptions<ApplicationDbContext> options,
        IProjectionProgramRunnerService runnerService,
        bool isClientConnected,
        IProjectionProgramService? projectionProgramService = null,
        IClientService? clientService = null)
    {
        return new TestableProjectionProgramControlActionExecutor(
            GetScopeFactoryMock(projectionProgramService, clientService).Object,
            GetFactoryMock(options).Object,
            runnerService,
            new Mock<ILogger<ProjectionProgramControlActionExecutor>>().Object,
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

    private static AutomationRuleAction GetAction(string parametersJson)
    {
        return new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            ActionType = AutomationActionTypes.ProjectionProgramControl,
            ParametersJson = parametersJson,
            SortOrder = 0,
            IsActive = true
        };
    }

    [TestMethod]
    public void CanHandle_OnlyHandlesProjectionProgramControl()
    {
        ProjectionProgramControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true);

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.ProjectionProgramControl));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.StartProjectionProgram));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.StopProjectionProgram));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.DmxSceneControl));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenParametersInvalidJson()
    {
        ProjectionProgramControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("not json"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.Contains("Projection program control failed", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenNotConfiguredWithAClient()
    {
        ProjectionProgramControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("{}"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a client", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenStartHasNoProjectionProgramId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Start\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a projection program", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotFoundInDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{Guid.NewGuid()}\",\"Operation\":\"Pause\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options, connectionId: "stale-connection");

        Mock<IProjectionProgramRunnerService> runnerMock = new();

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, runnerMock.Object, isClientConnected: false);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Pause\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_Start_ShouldTriggerProgram_WithDisplayIndex()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid programId = Guid.NewGuid();

        Mock<IProjectionProgramService> serviceMock = new();
        serviceMock.Setup(s => s.TriggerProjectionProgramAsync(programId, client.Id, 2))
            .ReturnsAsync(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, Mock.Of<IProjectionProgramRunnerService>(), isClientConnected: true, projectionProgramService: serviceMock.Object);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Start\",\"ProjectionProgramId\":\"{programId}\",\"DisplayIndex\":2}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        serviceMock.Verify(s => s.TriggerProjectionProgramAsync(programId, client.Id, 2), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_Pause_ShouldCallRunnerPause_AtDisplayZero_WhenDisplayIndexOmitted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Pause(client.Id, 0)).Returns(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Pause\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.Pause(client.Id, 0), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_Resume_ShouldCallRunnerResume()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Resume(client.Id, 1)).Returns(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Resume\",\"DisplayIndex\":1}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.Resume(client.Id, 1), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_SkipToNext_ShouldCallRunnerSkipToNextStep()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.SkipToNextStep(client.Id, 0)).Returns(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"SkipToNext\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.SkipToNextStep(client.Id, 0), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_SkipToPrevious_ShouldCallRunnerSkipToPreviousStep()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.SkipToPreviousStep(client.Id, 0)).Returns(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"SkipToPrevious\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.SkipToPreviousStep(client.Id, 0), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_Stop_ShouldStopTheRunAndCloseTheKioskWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Stop(client.Id, 2, null)).Returns(Result<bool>.Ok(true));

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(c => c.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 2))
            .ReturnsAsync(Result<bool>.Ok(true));

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, runnerMock.Object, isClientConnected: true, clientService: clientServiceMock.Object);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Stop\",\"DisplayIndex\":2}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);

        // Both halves matter: Stop alone raises OnProgramStopped, not OnProgramCompletedNaturally,
        // which is the only event the window-close listener watches - so stopping without closing
        // leaves the last frame frozen on the screen.
        runnerMock.Verify(r => r.Stop(client.Id, 2, null), Times.Once);
        clientServiceMock.Verify(c => c.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 2), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_Stop_ShouldNotCloseWindow_WhenStoppingTheRunFails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Stop(client.Id, 0, null)).Returns(Result<bool>.Fail("boom"));

        Mock<IClientService> clientServiceMock = new();

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, runnerMock.Object, isClientConnected: true, clientService: clientServiceMock.Object);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Stop\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("boom", error);
        clientServiceMock.Verify(c => c.CloseTemporaryProjectionWindowOnClientAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_Stop_ShouldFail_WhenClosingTheWindowFails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Stop(client.Id, 0, null)).Returns(Result<bool>.Ok(true));

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(c => c.CloseTemporaryProjectionWindowOnClientAsync(client.Id, 0))
            .ReturnsAsync(Result<bool>.Fail("window close failed"));

        ProjectionProgramControlActionExecutor executor = GetExecutor(
            options, runnerMock.Object, isClientConnected: true, clientService: clientServiceMock.Object);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Stop\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("window close failed", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnError_WhenRunnerReturnsFail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.Pause(client.Id, 0)).Returns(Result<bool>.Fail("Nothing is running"));

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Pause\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Nothing is running", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenOperationUnknown()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IProjectionProgramRunnerService> runnerMock = new();

        ProjectionProgramControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Explode\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Unknown projection program operation: Explode", error);
        runnerMock.VerifyNoOtherCalls();
    }
}
