#nullable enable

using System.Reflection;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static Mock<ISystemDbContextFactory> GetFactoryMock(
        DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();

        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    // A real MusicPlayerService rather than a mocked IMusicPlayerService: Play and Pause both bottom out in
    // _hubContext.Clients.Client(connectionId).SendCoreAsync(methodName, args), so the hub proxy is
    // the seam: a real MusicPlayerService over a mocked IHubContext lets the assertions name the
    // method actually dispatched ("Play" vs "Pause") instead of just that something happened.
    private static (MusicPlayerService Player, Mock<ISingleClientProxy> Proxy) GetMusicPlayer(
        ISystemDbContextFactory factory)
    {
        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IHubContext<SignalRControlHub>> hubMock = new();

        clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factory,
            BuildClientServiceScopeFactory(factory, hubMock.Object),
            NullLogger<MusicPlayerService>.Instance);

        return (player, proxyMock);
    }

    // MusicPlayerService resolves IClientService (scoped) per call via IServiceScopeFactory since
    // MusicPlayerService itself is a singleton in production - build a real minimal container here
    // so GetClientConnectionIdAsync exercises the same ClientService/cache lookup production does,
    // backed by the same InMemory context factory as the rest of the test.
    private static IServiceScopeFactory BuildClientServiceScopeFactory(
        ISystemDbContextFactory factory,
        IHubContext<SignalRControlHub> hubContext)
    {
        ServiceCollection services = new();

        services.AddSingleton(factory);
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        services.AddSingleton(hubContext);
        services.AddSingleton<ILogger<ClientService>>(NullLogger<ClientService>.Instance);
        services.AddMemoryCache();
        services.AddSingleton<IKioskFleetEventBus, KioskFleetEventBus>();
        services.AddScoped<IClientService, ClientService>();

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static MusicControlActionExecutor GetExecutor(
        ISystemDbContextFactory factory,
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

    private static async Task SeedPlaylistAsync(DbContextOptions<ApplicationDbContext> options, Guid playlistId, int songCount)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Game", CreateDate = DateTime.UtcNow });

        for (int i = 0; i < songCount; i++)
        {
            Guid songId = Guid.NewGuid();
            ctx.Songs.Add(new Song
            {
                Id = songId,
                Name = $"Song {i}",
                AlbumName = "Album",
                FilePath = $"song{i}.mp3",
                IsActive = true,
                CreateDate = DateTime.UtcNow
            });
            ctx.PlaylistSongMembers.Add(new PlaylistSongMember { SongId = songId, PlaylistId = playlistId, CreateDate = DateTime.UtcNow });
        }

        await ctx.SaveChangesAsync();
    }

    private async Task<(MusicPlayerService Player, Mock<ISingleClientProxy> Proxy, MusicControlActionExecutor Executor)>
        GetConnectedSetupAsync(DbContextOptions<ApplicationDbContext> options, Guid clientId)
    {
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
        string connectionId = NewConnectionId();
        await SeedClientAsync(options, clientId, connectionId);
        MarkConnected(connectionId);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);

        return (player, proxy, GetExecutor(factory.Object, player));
    }

    private static int Sent(Mock<ISingleClientProxy> proxy, string method) =>
        proxy.Invocations.Count(i => (string)i.Arguments[0] == method);

    [TestMethod]
    public async Task Play_WithResumeIfLoaded_CarriesOnWithThePausedPlaylistInsteadOfReloadingIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedPlaylistAsync(options, playlistId, 3);
        (MusicPlayerService player, Mock<ISingleClientProxy> proxy, MusicControlActionExecutor executor) =
            await GetConnectedSetupAsync(options, clientId);

        AutomationRuleAction play = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","PlaylistId":"{{playlistId}}","ResumeIfLoaded":true}""");

        Assert.IsTrue((await executor.ExecuteAsync(play, clientId)).Success);
        player.UpdatePlaybackState(clientId, NAudio.Wave.PlaybackState.Paused);
        Assert.IsTrue((await executor.ExecuteAsync(play, clientId)).Success);

        Assert.AreEqual(1, Sent(proxy, "LoadPlaylist"), "The second Play must resume, not load the playlist again.");
        Assert.AreEqual(2, Sent(proxy, "Play"));
    }

    [TestMethod]
    public async Task Play_WithResumeIfLoaded_LoadsThePlaylist_WhenADifferentOneIsLoadedOrItStopped()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedPlaylistAsync(options, other, 2);
        await SeedPlaylistAsync(options, playlistId, 2);
        (MusicPlayerService player, Mock<ISingleClientProxy> proxy, MusicControlActionExecutor executor) =
            await GetConnectedSetupAsync(options, clientId);

        await player.Play(clientId, other);
        player.UpdatePlaybackState(clientId, NAudio.Wave.PlaybackState.Paused);

        AutomationRuleAction play = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","PlaylistId":"{{playlistId}}","ResumeIfLoaded":true}""");

        Assert.IsTrue((await executor.ExecuteAsync(play, clientId)).Success);
        Assert.AreEqual(2, Sent(proxy, "LoadPlaylist"), "A different playlist was loaded, so this one has to be loaded.");
        Assert.AreEqual(playlistId, player.GetCurrentPlaylist(clientId)?.Id);

        player.UpdatePlaybackState(clientId, NAudio.Wave.PlaybackState.Stopped);
        Assert.IsTrue((await executor.ExecuteAsync(play, clientId)).Success);
        Assert.AreEqual(3, Sent(proxy, "LoadPlaylist"), "After a stop there's nothing to carry on with.");
    }

    [TestMethod]
    public async Task Play_WithoutResumeIfLoaded_StartsThePlaylistAgain()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedPlaylistAsync(options, playlistId, 2);
        (MusicPlayerService player, Mock<ISingleClientProxy> proxy, MusicControlActionExecutor executor) =
            await GetConnectedSetupAsync(options, clientId);

        AutomationRuleAction play = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","PlaylistId":"{{playlistId}}"}""");

        await executor.ExecuteAsync(play, clientId);
        player.UpdatePlaybackState(clientId, NAudio.Wave.PlaybackState.Paused);
        await executor.ExecuteAsync(play, clientId);

        Assert.AreEqual(2, Sent(proxy, "LoadPlaylist"), "Existing steps keep restarting the playlist, as before.");
    }

    [TestMethod]
    public async Task Play_SetsShuffleAndRepeat_WhenTheStepSaysTo()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        (MusicPlayerService player, _, MusicControlActionExecutor executor) = await GetConnectedSetupAsync(options, clientId);

        AutomationRuleAction play = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","Shuffle":"On","Repeat":"One"}""");

        Assert.IsTrue((await executor.ExecuteAsync(play, clientId)).Success);
        Assert.IsTrue(player.GetShuffleEnabled(clientId));
        Assert.AreEqual(RepeatMode.One, player.GetCurrentRepeatMode(clientId));

        AutomationRuleAction off = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","Shuffle":"Off","Repeat":"Off"}""");

        Assert.IsTrue((await executor.ExecuteAsync(off, clientId)).Success);
        Assert.IsFalse(player.GetShuffleEnabled(clientId));
        Assert.AreEqual(RepeatMode.Off, player.GetCurrentRepeatMode(clientId));
    }

    [TestMethod]
    public async Task ShuffleAndRepeat_AreLeftAlone_WhenNotSetOrOnPause()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        (MusicPlayerService player, _, MusicControlActionExecutor executor) = await GetConnectedSetupAsync(options, clientId);

        await player.SetShuffle(clientId, true);
        await player.SetRepeatMode(clientId, RepeatMode.One);

        await executor.ExecuteAsync(BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","Shuffle":null,"Repeat":null}"""), clientId);
        await executor.ExecuteAsync(BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Pause","Shuffle":"Off","Repeat":"Off"}"""), clientId);

        Assert.IsTrue(player.GetShuffleEnabled(clientId));
        Assert.AreEqual(RepeatMode.One, player.GetCurrentRepeatMode(clientId));
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);

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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
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
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);

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

    [TestMethod]
    public async Task ExecuteAsync_ShouldFailWithoutPlaying_WhenThePlaylistHasNoSongs()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<ISystemDbContextFactory> factory = GetFactoryMock(options);
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();

        string connectionId = NewConnectionId();
        await SeedClientAsync(options, clientId, connectionId);
        MarkConnected(connectionId);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Empty", CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetMusicPlayer(factory.Object);
        MusicControlActionExecutor executor = GetExecutor(factory.Object, player);

        AutomationRuleAction action = BuildAction(
            $$"""{"TargetClientId":"{{clientId}}","Operation":"Play","PlaylistId":"{{playlistId}}"}""");

        (bool success, string? error) = await executor.ExecuteAsync(action, clientId);

        Assert.IsFalse(success, "An empty playlist should be logged as a failed action, not a silent success.");
        Assert.IsNotNull(error);
        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
