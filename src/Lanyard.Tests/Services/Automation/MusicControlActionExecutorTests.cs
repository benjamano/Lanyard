#nullable enable

using System.Reflection;
using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class MusicControlActionExecutorTests
{
    // A fresh id per test. SignalRControlHub's registry is static and therefore shared by every
    // test in the assembly, so a fixed id lets one test's "connected" state decide another test's
    // outcome - which is exactly what happened with a shared constant: the "connection id is
    // absent" case saw the id a sibling test had registered and reported the client as connected.
    private readonly List<string> _registeredConnectionIds = [];

    private string NewConnectionId()
    {
        return $"test-connection-{Guid.NewGuid()}";
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(
        DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();

        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    // MusicPlayerService is concrete and its Play/Pause aren't virtual, so Moq can't stand in for
    // it or verify the calls. Both bottom out in
    // _hubContext.Clients.Client(connectionId).SendCoreAsync(methodName, args), so the hub proxy is
    // the seam: a real MusicPlayerService over a mocked IHubContext lets the assertions name the
    // method actually dispatched ("Play" vs "Pause") instead of just that something happened.
    private static (MusicPlayerService Player, Mock<ISingleClientProxy> Proxy) GetMusicPlayer(
        IDbContextFactory<ApplicationDbContext> factory)
    {
        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IHubContext<SignalRControlHub>> hubMock = new();

        clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factory,
            NullLogger<MusicPlayerService>.Instance);

        return (player, proxyMock);
    }

    private static MusicControlActionExecutor GetExecutor(
        IDbContextFactory<ApplicationDbContext> factory,
        MusicPlayerService player)
    {
        return new MusicControlActionExecutor(
            player,
            factory,
            NullLogger<MusicControlActionExecutor>.Instance);
    }

    private static AutomationRuleAction BuildAction(string parametersJson)
    {
        return new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = Guid.NewGuid(),
            ActionType = AutomationActionTypes.MusicControl,
            ParametersJson = parametersJson,
            IsActive = true
        };
    }

    // SignalRControlHub.ConnectedIds is a read-only view over a private static dictionary that is
    // only ever populated by OnConnectedAsync - which needs a real HttpContext, the shared client
    // secret, and ClientService before it will add anything. Driving that from a unit test isn't
    // realistic, so the registry is manipulated directly here.
    //
    // Reflection is the trade-off for not adding a test-only seam to a hub used in production. It
    // fails loudly rather than silently if the field is ever renamed or retyped: the assert below
    // turns a shape change into a clear test failure instead of tests that quietly stop covering
    // the connected path.
    private static IDictionary<string, bool> GetConnectionRegistry()
    {
        FieldInfo? field = typeof(SignalRControlHub)
            .GetField("_connections", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(
            field,
            "SignalRControlHub no longer has a private static '_connections' field. These tests "
            + "reach into it to simulate a connected kiosk; update this helper to match the new "
            + "shape, or the connected-client paths below stop being covered.");

        object? value = field.GetValue(null);

        Assert.IsInstanceOfType<IDictionary<string, bool>>(
            value,
            "SignalRControlHub._connections is no longer an IDictionary<string, bool>.");

        return (IDictionary<string, bool>)value!;
    }

    private void MarkConnected(string connectionId)
    {
        GetConnectionRegistry()[connectionId] = true;
        _registeredConnectionIds.Add(connectionId);
    }

    private static async Task SeedClientAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid clientId,
        string? connectionId)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Clients.Add(new Client
        {
            Id = clientId,
            Name = "Test Kiosk",
            MostRecentConnectionId = connectionId
        });

        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // The registry is static, so an entry left behind leaks into every other test in the
        // assembly, not just this class.
        IDictionary<string, bool> registry = GetConnectionRegistry();

        foreach (string connectionId in _registeredConnectionIds)
        {
            registry.Remove(connectionId);
        }

        _registeredConnectionIds.Clear();
    }

    [TestMethod]
    public void CanHandle_ShouldReturnTrue_ForMusicControl()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        (MusicPlayerService player, _) = GetMusicPlayer(factory.Object);

        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.MusicControl));
    }

    [TestMethod]
    [DataRow(AutomationActionTypes.StartProjectionProgram)]
    [DataRow(AutomationActionTypes.DmxSceneControl)]
    [DataRow("musiccontrol")]
    [DataRow("")]
    public void CanHandle_ShouldReturnFalse_ForOtherActionTypes(string actionType)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        (MusicPlayerService player, _) = GetMusicPlayer(factory.Object);

        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        // "musiccontrol" is in here deliberately: the match is an ordinal string comparison, so a
        // case difference must not handle the action.
        Assert.IsFalse(executor.CanHandle(actionType));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCallPlay_WhenOperationIsPlay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        Guid clientId = Guid.NewGuid();

        string connectionId = NewConnectionId();
        await SeedClientAsync(options, clientId, connectionId);
        MarkConnected(connectionId);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, clientId);

        Assert.IsTrue(success, error);
        Assert.IsNull(error);

        proxy.Verify(
            p => p.SendCoreAsync("Play", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCallPause_WhenOperationIsPause()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        Guid clientId = Guid.NewGuid();

        string connectionId = NewConnectionId();
        await SeedClientAsync(options, clientId, connectionId);
        MarkConnected(connectionId);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Pause"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, clientId);

        Assert.IsTrue(success, error);

        proxy.Verify(
            p => p.SendCoreAsync("Pause", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        proxy.Verify(
            p => p.SendCoreAsync("Play", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnClientNotConnected_WhenConnectionIdAbsentFromConnectedIds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        Guid clientId = Guid.NewGuid();

        // The client row exists and even remembers a connection id, but that connection is not in
        // the hub's live set - the kiosk was here and has since dropped. The id is never passed to
        // MarkConnected, and it is unique per test, so nothing else can have registered it.
        await SeedClientAsync(options, clientId, NewConnectionId());

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, clientId);

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);

        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnClientNotConnected_WhenClientNotFoundInDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        // No client seeded at all - the rule points at a target that has since been deleted.
        Guid missingClientId = Guid.NewGuid();

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{missingClientId}}","Operation":"Play"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, missingClientId);

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);

        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnActionTypeNotSupported_WhenOperationIsUnknown()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);
        Guid clientId = Guid.NewGuid();

        string connectionId = NewConnectionId();
        await SeedClientAsync(options, clientId, connectionId);
        MarkConnected(connectionId);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Rewind"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, clientId);

        Assert.IsFalse(success);
        Assert.AreEqual("Action type not supported: Rewind", error);

        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnMusicOperationFailed_WhenExceptionThrown()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = GetFactoryMock(options);

        (MusicPlayerService player, _) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        // Malformed JSON makes JsonSerializer.Deserialize throw, which is the general
        // "something blew up" path - it must come back as a failed Result rather than escaping to
        // the engine, because the engine treats a thrown action differently from a failed one.
        AutomationRuleAction action = BuildAction("{ this is not json");

        (bool success, string? error) = await executor.ExecuteAsync(action, Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.StartsWith("Music operation failed:", error);
    }
}
