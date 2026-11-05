using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static (PlaylistService Service, List<(int CompanyId, Guid? PlaylistId)> Events) GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        MusicLibraryEvents events = new(NullLogger<MusicLibraryEvents>.Instance);
        List<(int, Guid?)> raised = [];
        events.Changed += (company, playlist) => raised.Add((company, playlist));

        return (new PlaylistService(factoryMock.Object, events, NullLogger<PlaylistService>.Instance), raised);
    }

    // A playlist holding `count` songs at positions 0..count-1; returns the song ids in that order.
    private static async Task<(Guid PlaylistId, List<Guid> Songs)> SeedAsync(DbContextOptions<ApplicationDbContext> options, int count, int extraLibrarySongs = 0)
    {
        Guid playlistId = Guid.NewGuid();
        List<Guid> songs = [];

        await using ApplicationDbContext ctx = new(options);
        ctx.Playlists.Add(new Playlist { Id = playlistId, Name = "Lobby", CompanyId = 1, CreateDate = DateTime.UtcNow });

        for (int i = 0; i < count + extraLibrarySongs; i++)
        {
            Guid songId = Guid.NewGuid();
            ctx.Songs.Add(new Song { Id = songId, Name = $"Song {i}", AlbumName = "Album", FilePath = "x.mp3", DurationSeconds = 100, CreateDate = DateTime.UtcNow, IsActive = true, CompanyId = 1 });

            if (i < count)
            {
                ctx.PlaylistSongMembers.Add(new PlaylistSongMember { SongId = songId, PlaylistId = playlistId, CreateDate = DateTime.UtcNow, Position = i });
            }

            songs.Add(songId);
        }

        await ctx.SaveChangesAsync();

        return (playlistId, songs);
    }

    private static async Task<List<Guid>> OrderAsync(PlaylistService service, Guid playlistId) =>
        (await service.GetPlaylistTracksAsync(playlistId)).Data!.Select(x => x.SongId).ToList();

    [TestMethod]
    public async Task CreatePlaylistAsync_SavesThePlaylist_AndRejectsABlankName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (PlaylistService service, var events) = GetService(options);

        Result<Playlist> created = await service.CreatePlaylistAsync("  Lobby ", "Chill");
        Result<Playlist> blank = await service.CreatePlaylistAsync("  ", null);
        Result<Playlist> tooLong = await service.CreatePlaylistAsync(new string('x', PlaylistService.NameMaxLength + 1), null);

        Assert.IsTrue(created.IsSuccess, created.Error);
        Assert.IsFalse(blank.IsSuccess);
        Assert.IsFalse(tooLong.IsSuccess);
        Assert.AreEqual(1, events.Count, "Creating one tells open pages the list changed.");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual("Lobby", (await ctx.Playlists.SingleAsync()).Name);
    }

    [TestMethod]
    public async Task AddSong_AddsOnceAtTheEnd_ThenReportsAlreadyInPlaylist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, List<Guid> songs) = await SeedAsync(options, 2, extraLibrarySongs: 1);
        (PlaylistService service, var events) = GetService(options);

        Result<bool> first = await service.AddSongToPlaylistAsync(songs[2], playlistId);
        Result<bool> second = await service.AddSongToPlaylistAsync(songs[2], playlistId);

        Assert.IsTrue(first.IsSuccess && first.Data, first.Error);
        Assert.IsTrue(second.IsSuccess);
        Assert.IsFalse(second.Data, "A song already in the playlist must not be added twice.");
        CollectionAssert.AreEqual(songs, await OrderAsync(service, playlistId));
        CollectionAssert.Contains(events, (1, (Guid?)playlistId));
    }

    [TestMethod]
    public async Task AddSong_BringsBackASongThatWasRemoved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, List<Guid> songs) = await SeedAsync(options, 3);
        (PlaylistService service, _) = GetService(options);

        await service.RemoveSongFromPlaylistAsync(playlistId, songs[0]);
        Result<bool> readded = await service.AddSongToPlaylistAsync(songs[0], playlistId);

        Assert.IsTrue(readded.IsSuccess && readded.Data, readded.Error);
        CollectionAssert.AreEqual(new[] { songs[1], songs[2], songs[0] }, await OrderAsync(service, playlistId),
            "It comes back at the end, as a fresh add would.");
    }

    [TestMethod]
    public async Task AddSong_RefusesASongThatIsNotInTheLibrary()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 1);
        (PlaylistService service, _) = GetService(options);

        Result<bool> result = await service.AddSongToPlaylistAsync(Guid.NewGuid(), playlistId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task MoveSong_ReordersAndRenumbers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, List<Guid> songs) = await SeedAsync(options, 4);
        (PlaylistService service, var events) = GetService(options);

        Result<bool> moved = await service.MoveSongAsync(playlistId, songs[3], 1);

        Assert.IsTrue(moved.IsSuccess, moved.Error);
        CollectionAssert.AreEqual(new[] { songs[0], songs[3], songs[1], songs[2] }, await OrderAsync(service, playlistId));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, (await service.GetPlaylistTracksAsync(playlistId)).Data!.Select(x => x.Position).ToList());
        CollectionAssert.Contains(events, (1, (Guid?)playlistId));
    }

    [TestMethod]
    public async Task MoveSong_ClampsAnIndexPastEitherEnd()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, List<Guid> songs) = await SeedAsync(options, 3);
        (PlaylistService service, _) = GetService(options);

        await service.MoveSongAsync(playlistId, songs[0], 99);
        CollectionAssert.AreEqual(new[] { songs[1], songs[2], songs[0] }, await OrderAsync(service, playlistId));

        await service.MoveSongAsync(playlistId, songs[0], -5);
        CollectionAssert.AreEqual(songs, await OrderAsync(service, playlistId));
    }

    [TestMethod]
    public async Task Tracks_LeaveOutRemovedAndInactiveSongs()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, List<Guid> songs) = await SeedAsync(options, 3);
        (PlaylistService service, _) = GetService(options);

        await service.RemoveSongFromPlaylistAsync(playlistId, songs[0]);
        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.Songs.SingleAsync(x => x.Id == songs[1])).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        CollectionAssert.AreEqual(new[] { songs[2] }, await OrderAsync(service, playlistId));
    }

    [TestMethod]
    public async Task Overviews_AreByNameWithCountAndLength()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 3);
        (PlaylistService service, _) = GetService(options);
        await service.CreatePlaylistAsync("Afternoon", null);

        IReadOnlyList<PlaylistOverview> overviews = (await service.GetPlaylistOverviewsAsync()).Data!;

        CollectionAssert.AreEqual(new[] { "Afternoon", "Lobby" }, overviews.Select(x => x.Name).ToList());
        PlaylistOverview lobby = overviews.Single(x => x.Id == playlistId);
        Assert.AreEqual(3, lobby.SongCount);
        Assert.AreEqual(300, lobby.TotalSeconds);
        Assert.AreEqual(0, overviews.Single(x => x.Name == "Afternoon").SongCount);
    }

    [TestMethod]
    public async Task Rename_ChangesNameAndDescription()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 1);
        (PlaylistService service, _) = GetService(options);

        Result<bool> renamed = await service.RenamePlaylistAsync(playlistId, "Lobby evenings", "  ");
        Result<bool> blank = await service.RenamePlaylistAsync(playlistId, "", null);

        Assert.IsTrue(renamed.IsSuccess, renamed.Error);
        Assert.IsFalse(blank.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Playlist playlist = await ctx.Playlists.SingleAsync();
        Assert.AreEqual("Lobby evenings", playlist.Name);
        Assert.IsNull(playlist.Description);
    }

    [TestMethod]
    public async Task Delete_HidesThePlaylist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 1);
        (PlaylistService service, _) = GetService(options);

        Result<bool> deleted = await service.DeletePlaylistAsync(playlistId);

        Assert.IsTrue(deleted.IsSuccess, deleted.Error);
        Assert.AreEqual(0, (await service.GetPlaylistOverviewsAsync()).Data!.Count);
    }

    [TestMethod]
    public async Task Delete_IsRefusedWhileTheMusicScheduleUsesIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 1);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.MusicSchedules.Add(new MusicSchedule
            {
                Id = Guid.NewGuid(),
                LocationId = 1,
                IsEnabled = true,
                Dayparts =
                [
                    new MusicDaypart { Id = Guid.NewGuid(), Name = "Morning", DaysOfWeek = "0", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(10, 0), PlaylistId = playlistId, IsActive = true }
                ]
            });
            await ctx.SaveChangesAsync();
        }

        (PlaylistService service, _) = GetService(options);

        Result<bool> deleted = await service.DeletePlaylistAsync(playlistId);

        Assert.IsFalse(deleted.IsSuccess);
        StringAssert.Contains(deleted.Error, "Morning");
        Assert.AreEqual(1, (await service.GetPlaylistOverviewsAsync()).Data!.Count);
    }

    [TestMethod]
    public async Task Delete_IsRefusedWhileAnAutomationRulePlaysIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid playlistId, _) = await SeedAsync(options, 1);

        await using (ApplicationDbContext ctx = new(options))
        {
            Guid ruleId = Guid.NewGuid();
            ctx.AutomationRules.Add(new AutomationRule { Id = ruleId, Name = "Party starts", IsActive = true, IsEnabled = true, CompanyId = 1 });
            ctx.AutomationRuleActions.Add(new AutomationRuleAction
            {
                Id = Guid.NewGuid(),
                AutomationRuleId = ruleId,
                ActionType = AutomationActionTypes.MusicControl,
                ParametersJson = $$"""{"TargetClientId":"{{Guid.NewGuid()}}","Operation":"Play","PlaylistId":"{{playlistId}}"}""",
                IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        (PlaylistService service, _) = GetService(options);

        Result<bool> deleted = await service.DeletePlaylistAsync(playlistId);

        Assert.IsFalse(deleted.IsSuccess);
        StringAssert.Contains(deleted.Error, "Party starts");
    }
}
