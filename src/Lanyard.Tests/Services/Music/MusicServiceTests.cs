using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Music;

[TestClass]
public class MusicServiceTests
{
    private static async Task<MusicService> GetServiceAsync(params (string Name, string Album, bool IsActive)[] songs)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach ((string name, string album, bool isActive) in songs)
            {
                ctx.Songs.Add(new Song { Id = Guid.NewGuid(), Name = name, AlbumName = album, FilePath = "x.mp3", IsActive = isActive, CreateDate = DateTime.UtcNow, CompanyId = 1 });
            }

            await ctx.SaveChangesAsync();
        }

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        return new MusicService(factory.Object);
    }

    [TestMethod]
    public async Task Search_PagesThroughActiveSongsByName()
    {
        MusicService service = await GetServiceAsync(
            ("Charlie", "A", true), ("Alpha", "A", true), ("Bravo", "A", true), ("Delta", "A", true), ("Gone", "A", false));

        PagedResult<LibrarySong> first = (await service.SearchSongsAsync(null, 0, 2)).Data!;
        PagedResult<LibrarySong> second = (await service.SearchSongsAsync(null, 2, 2)).Data!;

        Assert.AreEqual(4, first.Total, "Inactive songs aren't in the library.");
        CollectionAssert.AreEqual(new[] { "Alpha", "Bravo" }, first.Items.Select(x => x.Name).ToList());
        CollectionAssert.AreEqual(new[] { "Charlie", "Delta" }, second.Items.Select(x => x.Name).ToList());
    }

    [TestMethod]
    public async Task Search_MatchesNameOrAlbumIgnoringCase()
    {
        MusicService service = await GetServiceAsync(
            ("Golden Hour", "Coastline", true), ("Neon Rivers", "Night Bus", true), ("Lemonade", "Big Weekend", true));

        PagedResult<LibrarySong> byName = (await service.SearchSongsAsync("  golden ", 0, 50)).Data!;
        PagedResult<LibrarySong> byAlbum = (await service.SearchSongsAsync("NIGHT", 0, 50)).Data!;

        CollectionAssert.AreEqual(new[] { "Golden Hour" }, byName.Items.Select(x => x.Name).ToList());
        CollectionAssert.AreEqual(new[] { "Neon Rivers" }, byAlbum.Items.Select(x => x.Name).ToList());
        Assert.AreEqual(1, byAlbum.Total);
    }

    [TestMethod]
    public async Task Search_CapsThePageSize()
    {
        MusicService service = await GetServiceAsync(Enumerable.Range(0, MusicService.MaxPageSize + 10).Select(i => ($"Song {i:000}", "A", true)).ToArray());

        PagedResult<LibrarySong> page = (await service.SearchSongsAsync(null, 0, 10_000)).Data!;

        Assert.AreEqual(MusicService.MaxPageSize, page.Items.Count);
        Assert.AreEqual(MusicService.MaxPageSize + 10, page.Total);
    }
}
