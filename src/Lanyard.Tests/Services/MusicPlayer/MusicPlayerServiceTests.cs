using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.MusicPlayer;

[TestClass]
public class MusicPlayerServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [TestMethod]
    public async Task Pause_ResolvesConnectionIdViaClientService_NotDirectDatabaseQuery()
    {
        Guid clientId = Guid.NewGuid();
        string connectionId = "connection-1";

        Mock<ISystemDbContextFactory> factoryMock = new();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IHubContext<SignalRControlHub>> hubMock = new();
        clientsMock.Setup(c => c.Client(connectionId)).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        // MusicPlayerService is a singleton, so it can't constructor-inject the scoped
        // IClientService - it resolves it per call via IServiceScopeFactory instead (same
        // pattern DmxService uses). This test asserts that path is actually taken, and that
        // GetClientConnectionIdAsync no longer queries the database directly itself.
        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientCurrentConnectionIdAsync(clientId))
            .ReturnsAsync(Result<string?>.Ok(connectionId));

        Mock<IServiceScope> scopeMock = new();
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientServiceMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factoryMock.Object,
            scopeFactoryMock.Object,
            NullLogger<MusicPlayerService>.Instance);

        await player.Pause(clientId);

        clientServiceMock.Verify(x => x.GetClientCurrentConnectionIdAsync(clientId), Times.Once);
        proxyMock.Verify(
            p => p.SendCoreAsync("Pause", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The DB context factory should never be invoked directly for connection resolution -
        // that's IClientService's job (behind its own cache), reached only via the scope above.
        factoryMock.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Pause_DoesNotSendCommand_WhenClientServiceCannotResolveConnection()
    {
        Guid clientId = Guid.NewGuid();

        Mock<ISystemDbContextFactory> factoryMock = new();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IHubContext<SignalRControlHub>> hubMock = new();
        Mock<IHubClients> clientsMock = new();
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientCurrentConnectionIdAsync(clientId))
            .ReturnsAsync(Result<string?>.Ok(null));

        Mock<IServiceScope> scopeMock = new();
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientServiceMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factoryMock.Object,
            scopeFactoryMock.Object,
            NullLogger<MusicPlayerService>.Instance);

        await player.Pause(clientId);

        clientsMock.Verify(c => c.Client(It.IsAny<string>()), Times.Never);
    }

    private static (MusicPlayerService Player, Mock<ISingleClientProxy> Proxy) GetConnectedPlayer(
        DbContextOptions<ApplicationDbContext> options, Guid clientId)
    {
        const string connectionId = "connection-1";

        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IHubContext<SignalRControlHub>> hubMock = new();
        clientsMock.Setup(c => c.Client(connectionId)).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientCurrentConnectionIdAsync(clientId))
            .ReturnsAsync(Result<string?>.Ok(connectionId));

        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientServiceMock.Object);
        Mock<IServiceScope> scopeMock = new();
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);
        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factoryMock.Object,
            scopeFactoryMock.Object,
            NullLogger<MusicPlayerService>.Instance);

        return (player, proxyMock);
    }

    private static async Task SeedPlaylistAsync(
        DbContextOptions<ApplicationDbContext> options, Guid playlistId, params (Guid Id, bool IsActive)[] songs)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Lobby", CreateDate = DateTime.UtcNow });

        foreach ((Guid songId, bool isActive) in songs)
        {
            ctx.Songs.Add(new Song
            {
                Id = songId,
                Name = "Song",
                AlbumName = "Album",
                FilePath = "song.mp3",
                IsActive = isActive,
                CreateDate = DateTime.UtcNow
            });
            ctx.PlaylistSongMembers.Add(new PlaylistSongMember { SongId = songId, PlaylistId = playlistId, CreateDate = DateTime.UtcNow });
        }

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task PlayPlaylist_FailsWithoutSendingAnything_WhenThePlaylistHasNoActiveSongs()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();

        // Only an inactive song - nothing left to play.
        await SeedPlaylistAsync(options, playlistId, (Guid.NewGuid(), false));

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        Result<bool> result = await player.Play(clientId, playlistId);

        Assert.IsFalse(result.IsSuccess);
        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "An empty LoadPlaylist crashes older kiosks, so nothing may be sent.");
        Assert.IsNull(player.GetCurrentPlaylist(clientId), "A refused playlist must not replace the queue.");
    }

    [TestMethod]
    public async Task PlayPlaylist_LoadsThenPlays_WhenThePlaylistHasSongs()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        Guid songId = Guid.NewGuid();

        await SeedPlaylistAsync(options, playlistId, (songId, true));

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        Result<bool> result = await player.Play(clientId, playlistId);

        Assert.IsTrue(result.IsSuccess);
        proxy.Verify(p => p.SendCoreAsync("LoadPlaylist", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        proxy.Verify(p => p.SendCoreAsync("Play", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(playlistId, player.GetCurrentPlaylist(clientId)?.Id);
        Assert.AreEqual(songId, player.GetCurrentSong(clientId)?.Id);
    }

    [TestMethod]
    public void ReportVolumeFromClient_RecordsTheVolumeWithoutSendingItBack()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        player.ReportVolumeFromClient(clientId, 140);

        Assert.AreEqual(100, player.GetCurrentVolume(clientId));
        proxy.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task HandleSongEnded_SwitchesToAQueuedPlaylistInsteadOfTheNextSong()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid current = Guid.NewGuid();
        Guid queued = Guid.NewGuid();
        Guid queuedSong = Guid.NewGuid();

        await SeedPlaylistAsync(options, current, (Guid.NewGuid(), true), (Guid.NewGuid(), true));
        await SeedPlaylistAsync(options, queued, (queuedSong, true));

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);
        await player.Play(clientId, current);
        proxy.Invocations.Clear();

        player.SetPendingPlaylist(clientId, queued);
        await player.HandleSongEndedAsync(clientId);

        Assert.AreEqual(queued, player.GetCurrentPlaylist(clientId)?.Id);
        Assert.AreEqual(queuedSong, player.GetCurrentSong(clientId)?.Id);
        Assert.IsNull(player.GetPendingPlaylist(clientId), "The switch is used up.");
        proxy.Verify(p => p.SendCoreAsync("LoadPlaylist", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task HandleSongEnded_CarriesOnWithTheQueueWhenTheQueuedPlaylistIsEmpty()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid current = Guid.NewGuid();
        Guid empty = Guid.NewGuid();

        await SeedPlaylistAsync(options, current, (Guid.NewGuid(), true), (Guid.NewGuid(), true));
        await SeedPlaylistAsync(options, empty);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);
        await player.Play(clientId, current);
        proxy.Invocations.Clear();

        player.SetPendingPlaylist(clientId, empty);
        await player.HandleSongEndedAsync(clientId);

        Assert.AreEqual(current, player.GetCurrentPlaylist(clientId)?.Id);
        proxy.Verify(p => p.SendCoreAsync("Load", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once,
            "It should move to the next song rather than fall silent.");
    }

    [TestMethod]
    public async Task PlayingAPlaylistByHand_CancelsAQueuedSwitch()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid chosen = Guid.NewGuid();

        await SeedPlaylistAsync(options, chosen, (Guid.NewGuid(), true));

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        player.SetPendingPlaylist(clientId, Guid.NewGuid());

        await player.Play(clientId, chosen);

        Assert.IsNull(player.GetPendingPlaylist(clientId));
    }

    [TestMethod]
    public async Task ManualCommands_RaiseOnManualMusicChange_ScheduleCommandsDoNot()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        List<Guid> manual = [];
        player.OnManualMusicChange += manual.Add;

        await player.Pause(clientId, MusicCommandSource.Schedule);
        await player.SetVolume(clientId, 40, MusicCommandSource.Schedule);
        Assert.AreEqual(0, manual.Count);

        await player.Pause(clientId);
        await player.SetVolume(clientId, 40);
        Assert.AreEqual(2, manual.Count);
    }

    [TestMethod]
    public async Task SkippingASong_IsNotAChangeByHand()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedPlaylistAsync(options, playlistId, (Guid.NewGuid(), true), (Guid.NewGuid(), true));

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.Play(clientId, playlistId, MusicCommandSource.Schedule);

        List<Guid> manual = [];
        player.OnManualMusicChange += manual.Add;

        await player.Next(clientId);
        await player.Seek(clientId, 10);

        Assert.AreEqual(0, manual.Count);
    }
}
