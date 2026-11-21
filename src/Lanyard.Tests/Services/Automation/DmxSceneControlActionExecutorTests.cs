using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
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
        ISystemDbContextFactory contextFactory,
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

    private static Mock<ISystemDbContextFactory> GetFactoryMock(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
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

    // Each test has its own in-memory database, so one fixed rule id is enough.
    private static readonly Guid RuleId = Guid.Parse("4a6f0d55-1c55-4c44-9f31-0d3b8c1d2e01");

    private const int CompanyId = 3;

    private static async Task<Client> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, string? connectionId = "connection-1", int clientCompanyId = CompanyId)
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = clientCompanyId,
            Name = "Test Client",
            MostRecentConnectionId = connectionId,
            CreateDate = DateTime.UtcNow
        };

        ctx.Clients.Add(client);

        if (!await ctx.AutomationRules.AnyAsync(r => r.Id == RuleId))
        {
            ctx.AutomationRules.Add(new AutomationRule { Id = RuleId, CompanyId = CompanyId, Name = "Rule", TriggerClientId = client.Id });
        }

        await ctx.SaveChangesAsync();

        return client;
    }

    private static async Task<Guid> SeedSceneAsync(DbContextOptions<ApplicationDbContext> options, Guid clientId)
    {
        await using ApplicationDbContext ctx = new(options);

        Lanyard.Infrastructure.Models.Dmx.DmxScene scene = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = CompanyId,
            ClientId = clientId,
            Name = "Scene",
            IsActive = true,
            CreateByUserId = "test"
        };

        ctx.DmxScenes.Add(scene);
        await ctx.SaveChangesAsync();

        return scene.Id;
    }

    private static AutomationRuleAction GetAction(string parametersJson)
    {
        return new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = RuleId,
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
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId, null))
            .ReturnsAsync(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        Assert.IsNull(error);

        // No HoldMilliseconds in the JSON is how every rule authored before hold support
        // reads back, and it must still mean "run until stopped".
        runnerMock.Verify(r => r.StartSceneAsync(client.Id, sceneId, null), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldPassHoldDurationToRunner_WhenStartingScene()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId, TimeSpan.FromMilliseconds(3000)))
            .ReturnsAsync(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson =
            $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\",\"HoldMilliseconds\":3000}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.StartSceneAsync(client.Id, sceneId, TimeSpan.FromMilliseconds(3000)), Times.Once);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task ExecuteAsync_ShouldTreatNonPositiveHoldAsNoHold(int holdMilliseconds)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId, null))
            .ReturnsAsync(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson =
            $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\",\"HoldMilliseconds\":{holdMilliseconds}}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);

        // A zero or negative hold must not become a CancelAfter that kills the scene instantly.
        runnerMock.Verify(r => r.StartSceneAsync(client.Id, sceneId, null), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldIgnoreHoldDuration_WhenStoppingScene()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StopScene(sceneId)).Returns(Result<bool>.Ok(true));

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson =
            $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StopScene\",\"SceneId\":\"{sceneId}\",\"HoldMilliseconds\":3000}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        runnerMock.Verify(r => r.StopScene(sceneId), Times.Once);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldStopScene()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid sceneId = await SeedSceneAsync(options, client.Id);

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
        Guid sceneId = await SeedSceneAsync(options, client.Id);

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
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();
        runnerMock.Setup(r => r.StartSceneAsync(client.Id, sceneId, It.IsAny<TimeSpan?>()))
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
        Guid sceneId = await SeedSceneAsync(options, client.Id);

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

    [TestMethod]
    public async Task ExecuteAsync_ShouldRefuse_AKioskFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options, clientCompanyId: 99);
        Guid sceneId = await SeedSceneAsync(options, client.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("The action's kiosk belongs to a different company", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("StartScene")]
    [DataRow("StopScene")]
    public async Task ExecuteAsync_ShouldRefuse_ASceneFromAnotherKiosk(string operation)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Client otherKiosk = await SeedClientAsync(options);
        Guid sceneId = await SeedSceneAsync(options, otherKiosk.Id);

        Mock<IDmxSceneRunnerService> runnerMock = new();

        DmxSceneControlActionExecutor executor = GetExecutor(options, runnerMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"Operation\":\"{operation}\",\"SceneId\":\"{sceneId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("The action's DMX scene no longer exists on that kiosk", error);
        runnerMock.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void TryReadTargets_ReadsTheKioskAndScene_AndNoSceneForStopAll()
    {
        Guid clientId = Guid.NewGuid();
        Guid sceneId = Guid.NewGuid();

        Assert.IsTrue(DmxSceneControlActionExecutor.TryReadTargets($"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"StartScene\",\"SceneId\":\"{sceneId}\"}}", out Guid readClient, out Guid? readScene));
        Assert.AreEqual(clientId, readClient);
        Assert.AreEqual(sceneId, readScene);

        Assert.IsTrue(DmxSceneControlActionExecutor.TryReadTargets($"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"StopAllScenes\",\"SceneId\":\"{sceneId}\"}}", out _, out Guid? stopAllScene));
        Assert.IsNull(stopAllScene);

        Assert.IsFalse(DmxSceneControlActionExecutor.TryReadTargets("not json", out _, out _));
    }
}
