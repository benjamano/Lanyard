using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Music;

[TestClass]
public class PlaylistServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static PlaylistService GetService(DbContextOptions<ApplicationDbContext> options, Mock<ISongAnalysisQueue>? queue = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new PlaylistService(factoryMock.Object, (queue ?? new Mock<ISongAnalysisQueue>()).Object);
    }

    [TestMethod]
    public async Task CreatePlaylistAsync_SavesThePlaylist_AndRejectsABlankName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        PlaylistService service = GetService(options);

        Result<Playlist> created = await service.CreatePlaylistAsync("Lobby", "Chill");
        Result<Playlist> blank = await service.CreatePlaylistAsync("  ", null);

        Assert.IsTrue(created.IsSuccess, created.Error);
        Assert.IsFalse(blank.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.Playlists.CountAsync());
        Assert.AreEqual("Lobby", (await ctx.Playlists.SingleAsync()).Name);
    }

    [TestMethod]
    public async Task AddSongToPlaylistAsync_AddsOnce_ThenReportsAlreadyInPlaylist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Song song = new() { Id = Guid.NewGuid(), Name = "Track", AlbumName = "Album", FilePath = "/x.mp3", CreateDate = DateTime.UtcNow, IsActive = true };
        Guid playlistId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Songs.Add(song);
            ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Lobby", CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Mock<ISongAnalysisQueue> queue = new();
        PlaylistService service = GetService(options, queue);

        Result<bool> first = await service.AddSongToPlaylistAsync(song, playlistId);
        Result<bool> second = await service.AddSongToPlaylistAsync(song, playlistId);

        Assert.IsTrue(first.IsSuccess && first.Data, first.Error);
        Assert.IsTrue(second.IsSuccess);
        Assert.IsFalse(second.Data, "A song already in the playlist must not be added twice.");
        queue.Verify(q => q.Enqueue(It.IsAny<Guid>()), Times.Never, "An existing song must not be re-queued for analysis.");

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(1, await check.PlaylistSongMembers.CountAsync(m => m.PlaylistId == playlistId && m.SongId == song.Id));
    }

    [TestMethod]
    public async Task AddSongToPlaylistAsync_UnknownSong_IsPersistedAndQueuedForAnalysis()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid playlistId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Lobby", CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Mock<ISongAnalysisQueue> queue = new();
        PlaylistService service = GetService(options, queue);

        // A dev-scanned local file: not in the Songs table yet.
        Song scanned = new() { Id = Guid.NewGuid(), Name = "Local", AlbumName = "Disk", FilePath = "/local.mp3", DurationSeconds = 90 };

        Result<bool> result = await service.AddSongToPlaylistAsync(scanned, playlistId);

        Assert.IsTrue(result.IsSuccess && result.Data, result.Error);

        await using ApplicationDbContext check = new(options);
        Song persisted = await check.Songs.SingleAsync();
        Assert.AreEqual("Local", persisted.Name);
        Assert.AreEqual(1, await check.PlaylistSongMembers.CountAsync(m => m.PlaylistId == playlistId && m.SongId == persisted.Id));
        queue.Verify(q => q.Enqueue(persisted.Id), Times.Once);
    }
}
