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

    // Songs with distinct names and dates, returned in playlist order (newest first).
    private static async Task<List<Guid>> SeedOrderedPlaylistAsync(
        DbContextOptions<ApplicationDbContext> options, Guid playlistId, int count)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Lobby", CreateDate = DateTime.UtcNow });

        List<Guid> ids = [];
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            Guid songId = Guid.NewGuid();
            ids.Add(songId);
            ctx.Songs.Add(new Song
            {
                Id = songId,
                Name = $"Song {i:00}",
                AlbumName = "Album",
                FilePath = "song.mp3",
                IsActive = true,
                DurationSeconds = 180,
                CreateDate = start.AddDays(-i)
            });
            ctx.PlaylistSongMembers.Add(new PlaylistSongMember { SongId = songId, PlaylistId = playlistId, CreateDate = DateTime.UtcNow });
        }

        await ctx.SaveChangesAsync();

        return ids;
    }

    private static Dictionary<Guid, Guid> SentQueue(Mock<ISingleClientProxy> proxy) =>
        (Dictionary<Guid, Guid>)((object?[])proxy.Invocations
            .Last(x => (string)x.Arguments[0] == "LoadPlaylist")
            .Arguments[1])[0]!;

    [TestMethod]
    public async Task Shuffle_PlaysEverySongOnceBeforeAnyRepeats()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 8);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.ShufflePlay(clientId, playlistId);

        List<Guid> played = [player.GetCurrentSong(clientId)!.Id];
        for (int i = 0; i < songs.Count - 1; i++)
        {
            await player.Next(clientId);
            played.Add(player.GetCurrentSong(clientId)!.Id);
        }

        CollectionAssert.AreEquivalent(songs, played, "A shuffled lap plays each song exactly once.");
        Assert.IsTrue(player.GetShuffleEnabled(clientId));
    }

    [TestMethod]
    public async Task Previous_UnderShuffle_GoesBackToTheSongThatActuallyPlayed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedOrderedPlaylistAsync(options, playlistId, 10);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.ShufflePlay(clientId, playlistId);

        Guid first = player.GetCurrentSong(clientId)!.Id;
        await player.Next(clientId);
        Guid second = player.GetCurrentSong(clientId)!.Id;
        await player.Next(clientId);

        await player.Previous(clientId);
        Assert.AreEqual(second, player.GetCurrentSong(clientId)!.Id);
        await player.Previous(clientId);
        Assert.AreEqual(first, player.GetCurrentSong(clientId)!.Id);
    }

    [TestMethod]
    public async Task TurningShuffleOff_CarriesOnInPlaylistOrderFromTheCurrentSong()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 6);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.ShufflePlay(clientId, playlistId);
        await player.ToggleShuffle(clientId);

        int current = songs.IndexOf(player.GetCurrentSong(clientId)!.Id);
        await player.Next(clientId);

        Assert.AreEqual(songs[(current + 1) % songs.Count], player.GetCurrentSong(clientId)!.Id);
    }

    [TestMethod]
    public async Task PlaySong_FromAPlaylist_StartsThereAndCarriesOnInPlaylistOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 5);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        Result<bool> result = await player.PlaySong(clientId, songs[2], playlistId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(songs[2], player.GetCurrentSong(clientId)!.Id);
        CollectionAssert.AreEqual(
            new[] { songs[2], songs[3], songs[4], songs[0], songs[1] },
            SentQueue(proxy).Keys.ToList(),
            "The kiosk gets the queue in play order from the chosen song.");
        proxy.Verify(p => p.SendCoreAsync("Load", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never,
            "LoadPlaylist loads the first song itself; a separate Load would load it twice.");

        await player.Next(clientId);
        Assert.AreEqual(songs[3], player.GetCurrentSong(clientId)!.Id);
    }

    [TestMethod]
    public async Task PlaySong_WithoutAPlaylist_PlaysJustThatSong()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 3);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        Result<bool> result = await player.PlaySong(clientId, songs[1], playlistId: null);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { songs[1] }, SentQueue(proxy).Keys.ToList());
        Assert.IsNull(player.GetCurrentPlaylist(clientId));
        proxy.Verify(p => p.SendCoreAsync("Play", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PlaySong_FailsForASongThatIsGone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);

        Result<bool> result = await player.PlaySong(clientId, Guid.NewGuid(), playlistId: null);

        Assert.IsFalse(result.IsSuccess);
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task KioskReportingTheSameQueue_DoesNotMoveTheCurrentSong()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 4);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.PlaySong(clientId, songs[2], playlistId);

        // The kiosk reports the songs back in some other order when it reconnects.
        await player.SetQueue(clientId, [songs[3], songs[0], songs[1], songs[2]], playlistId);

        Assert.AreEqual(songs[2], player.GetCurrentSong(clientId)!.Id);
        await player.Next(clientId);
        Assert.AreEqual(songs[3], player.GetCurrentSong(clientId)!.Id, "The server's order still stands.");
    }

    [TestMethod]
    public async Task KioskReportingANewQueue_IsTakenInTheKiosksOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 4);

        // As after a server restart: the server knows nothing until the kiosk tells it.
        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.SetQueue(clientId, [songs[3], songs[1], songs[0]], playlistId);
        await player.UpdateCurrentSongAsync(clientId, songs[1]);

        Assert.AreEqual(songs[1], player.GetCurrentSong(clientId)!.Id);
        Assert.AreEqual(playlistId, player.GetCurrentPlaylist(clientId)?.Id);

        await player.Next(clientId);
        Assert.AreEqual(songs[0], player.GetCurrentSong(clientId)!.Id);
    }

    [TestMethod]
    public async Task SongEndedReportedTwice_AdvancesOnlyOnce()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 4);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.Play(clientId, playlistId);

        await player.HandleSongEndedAsync(clientId);
        await player.HandleSongEndedAsync(clientId);

        Assert.AreEqual(songs[1], player.GetCurrentSong(clientId)!.Id);
    }

    [TestMethod]
    public async Task Snapshot_ShowsTheSongPlaylistAndWhatsUpNext()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = await SeedOrderedPlaylistAsync(options, playlistId, 8);

        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);
        await player.Play(clientId, playlistId);
        player.UpdatePlaybackState(clientId, NAudio.Wave.PlaybackState.Playing);
        player.ReportVolumeFromClient(clientId, 35);

        MusicNowPlayingSnapshot snapshot = player.GetSnapshot(clientId);

        Assert.AreEqual(songs[0], snapshot.Song?.Id);
        Assert.AreEqual("Lobby", snapshot.PlaylistName);
        Assert.AreEqual(NAudio.Wave.PlaybackState.Playing, snapshot.State);
        Assert.AreEqual(180, snapshot.LengthSeconds);
        Assert.AreEqual(35, snapshot.Volume);
        Assert.IsTrue(snapshot.IsVolumeKnown);
        CollectionAssert.AreEqual(songs.Skip(1).Take(5).ToList(), snapshot.UpNext.Select(x => x.Id).ToList());
    }

    [TestMethod]
    public void Snapshot_OfAKioskWithNothingPlaying_IsEmptyAndDoesNotCreateState()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);

        IReadOnlyList<MusicNowPlayingSnapshot> snapshots = player.GetSnapshots([clientId]);

        Assert.AreEqual(1, snapshots.Count);
        Assert.IsNull(snapshots[0].Song);
        Assert.IsFalse(player.IsVolumeKnown(clientId));
    }

    [TestMethod]
    public async Task Disconnecting_KeepsShuffleAndTellsScreens()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);

        await player.ToggleShuffle(clientId);

        List<Guid> changes = [];
        player.OnNowPlayingChanged += changes.Add;

        player.MarkClientDisconnected(clientId);

        Assert.IsFalse(player.GetSnapshot(clientId).IsConnected);
        Assert.IsTrue(player.GetShuffleEnabled(clientId), "A dropped connection shouldn't reset shuffle.");
        Assert.AreEqual(1, changes.Count);

        player.MarkClientConnected(clientId);
        Assert.IsTrue(player.GetSnapshot(clientId).IsConnected);
    }

    [TestMethod]
    public void RoutinePositionReports_DoNotWakeScreens_ButAJumpDoes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        (MusicPlayerService player, _) = GetConnectedPlayer(options, clientId);

        player.UpdatePlaybackPosition(clientId, 10);

        List<Guid> changes = [];
        player.OnNowPlayingChanged += changes.Add;

        player.UpdatePlaybackPosition(clientId, 10.5);
        Assert.AreEqual(0, changes.Count);

        player.UpdatePlaybackPosition(clientId, 60);
        Assert.AreEqual(1, changes.Count);
    }

    [TestMethod]
    public async Task ASubscriberThatThrows_DoesNotBreakTheCommand()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid playlistId = Guid.NewGuid();
        await SeedOrderedPlaylistAsync(options, playlistId, 2);

        (MusicPlayerService player, Mock<ISingleClientProxy> proxy) = GetConnectedPlayer(options, clientId);
        player.OnNowPlayingChanged += _ => throw new InvalidOperationException("broken screen");

        Result<bool> result = await player.Play(clientId, playlistId);

        Assert.IsTrue(result.IsSuccess);
        proxy.Verify(p => p.SendCoreAsync("Play", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
