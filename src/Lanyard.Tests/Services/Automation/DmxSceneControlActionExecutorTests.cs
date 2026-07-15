using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class DmxSceneControlActionExecutorTests
{
    private sealed class TestableDmxSceneControlActionExecutor(
        IDmxSceneRunnerService sceneRunner,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        ILogger<DmxSceneControlActionExecutor> logger,
        bool isClientConnected) : DmxSceneControlActionExecutor(sceneRunner, contextFactory, logger)
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

    private static DmxSceneControlActionExecutor GetExecutor(
        DbContextOptions<ApplicationDbContext> options,
        IDmxSceneRunnerService sceneRunner,
        bool isClientConnected)
    {
        return new TestableDmxSceneControlActionExecutor(
            sceneRunner,
            GetFactoryMock(options).Object,
            new Mock<ILogger<DmxSceneControlActionExecutor>>().Object,
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
            ActionType = AutomationActionTypes.DmxSceneControl,
            ParametersJson = parametersJson,
            SortOrder = 0,
            IsActive = true
        };
    }

    [TestMethod]
    public void CanHandle_ShouldReturnTrue_ForDmxSceneControlOnly()
    {
        DmxSceneControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), new Mock<IDmxSceneRunnerService>().Object, isClientConnected: true);

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.DmxSceneControl));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.MusicControl));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.StartProjectionProgram));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldStartScene_WhenClientConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = Guid.NewGuid();

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId))
            .ReturnsAsync(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        Assert.IsNull(error);
        runnerMock.Verify(r => r.StartSceneAsync(client.Id, sceneId), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldStopScene()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = Guid.NewGuid();

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StopScene(sceneId)).Returns(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StopScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.StopScene(sceneId), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldStopAllScenes_WithoutASceneId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StopAllScenesForClient(client.Id)).Returns(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StopAllScenes\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.StopAllScenesForClient(client.Id), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenStartSceneHasNoSceneId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);

        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a DMX scene", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenNotConfiguredWithAClient()
    {
        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), runnerMock.Object, isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("{}"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a client", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options, connectionId: "stale-connection");
        Guid sceneId = Guid.NewGuid();

        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: false);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotFoundInDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{Guid.NewGuid()}\",\"Operation\":\"StartScene\",\"SceneId\":\"{Guid.NewGuid()}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnError_WhenRunnerReturnsFail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = Guid.NewGuid();

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId))
            .ReturnsAsync(Result<bool>.Fail("Scene has no steps"));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Scene has no steps", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenOperationUnknown()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = Guid.NewGuid();

        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"Explode\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Unknown DMX operation: Explode", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenParametersInvalidJson()
    {
        DmxSceneControlActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), new Mock<IDmxSceneRunnerService>().Object, isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("not json"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.Contains("DMX scene control failed", error);
    }
}
