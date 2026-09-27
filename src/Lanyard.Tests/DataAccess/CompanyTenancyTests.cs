using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.DataAccess;

// ApplicationDbContext's company filter and write stamping - the guarantee the public demo company
// relies on to never see or touch a real customer's rows.
[TestClass]
public class CompanyTenancyTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    // A signed-in caller: IsSystem is false, and a null CompanyId means "couldn't be resolved".
    private sealed class SignedInTenant(int? companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static async Task SeedTwoCompaniesAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Client clientA = new() { Id = Guid.NewGuid(), Name = "A kiosk", CompanyId = CompanyA };
        Client clientB = new() { Id = Guid.NewGuid(), Name = "B kiosk", CompanyId = CompanyB };
        DmxScene sceneA = new() { Id = Guid.NewGuid(), Name = "A scene", ClientId = clientA.Id, CompanyId = CompanyA, CreateByUserId = "u" };

        ctx.Clients.AddRange(clientA, clientB);
        ctx.Songs.AddRange(
            new Song { Id = Guid.NewGuid(), Name = "A song", AlbumName = "A", FilePath = "song.mp3", CompanyId = CompanyA },
            new Song { Id = Guid.NewGuid(), Name = "B song", AlbumName = "B", FilePath = "song.mp3", CompanyId = CompanyB });
        ctx.DmxScenes.Add(sceneA);
        ctx.DmxSceneSteps.Add(new DmxSceneStep { Id = Guid.NewGuid(), SceneId = sceneA.Id, CreateByUserId = "u" });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task SignedInCaller_OnlySeesTheirOwnCompanysRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        await using ApplicationDbContext ctx = new(options, new SignedInTenant(CompanyB));

        List<string> songs = await ctx.Songs.Select(x => x.Name).ToListAsync();
        List<string> clients = await ctx.Clients.Select(x => x.Name).ToListAsync();

        CollectionAssert.AreEqual(new[] { "B song" }, songs);
        CollectionAssert.AreEqual(new[] { "B kiosk" }, clients);
    }

    [TestMethod]
    public async Task SignedInCaller_CannotReachAnotherCompanysChildRowsDirectly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        await using ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB));
        await using ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA));

        Assert.AreEqual(0, await asB.DmxSceneSteps.CountAsync(), "Company B must not see company A's scene steps even without going through the scene.");
        Assert.AreEqual(1, await asA.DmxSceneSteps.CountAsync());
    }

    [TestMethod]
    public async Task SignedInCallerWithNoCompany_SeesNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        await using ApplicationDbContext ctx = new(options, new SignedInTenant(null));

        Assert.AreEqual(0, await ctx.Songs.CountAsync());
        Assert.AreEqual(0, await ctx.Clients.CountAsync());
    }

    [TestMethod]
    public async Task SystemContext_SeesEveryCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        await using ApplicationDbContext system = new(options);
        await using ApplicationDbContext explicitSystem = new(options, FixedTenantProvider.System);

        Assert.AreEqual(2, await system.Songs.CountAsync());
        Assert.AreEqual(2, await explicitSystem.Songs.CountAsync());
    }

    [TestMethod]
    public async Task NewRows_AreStampedWithTheCallersCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options, new SignedInTenant(CompanyB)))
        {
            ctx.Playlists.Add(new Playlist { Id = Guid.NewGuid(), Name = "B playlist" });
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext system = new(options);
        Playlist saved = await system.Playlists.SingleAsync();

        Assert.AreEqual(CompanyB, saved.CompanyId);
    }

    [TestMethod]
    public async Task NewRowsFromSystemCallers_DefaultToPlay2Day()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.Add(new Client { Id = Guid.NewGuid(), Name = "Auto-registered kiosk" });
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext system = new(options);

        Assert.AreEqual(ApplicationDbContext.SeedPlay2DayCompanyId, (await system.Clients.SingleAsync()).CompanyId);
    }

    [TestMethod]
    public async Task CreatingARowInAnotherCompany_Throws()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext ctx = new(options, new SignedInTenant(CompanyB));
        ctx.Songs.Add(new Song { Id = Guid.NewGuid(), Name = "Smuggled", AlbumName = "X", FilePath = "song.mp3", CompanyId = CompanyA });

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => ctx.SaveChangesAsync());
    }

    [TestMethod]
    public async Task CreatingARowWithNoResolvableCompany_Throws()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext ctx = new(options, new SignedInTenant(null));
        ctx.Songs.Add(new Song { Id = Guid.NewGuid(), Name = "Orphan", AlbumName = "X", FilePath = "song.mp3" });

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => ctx.SaveChangesAsync());
    }

    [TestMethod]
    public async Task UpdatingOrDeletingAnotherCompanysRow_Throws()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        Song companyASong;
        await using (ApplicationDbContext system = new(options))
        {
            companyASong = await system.Songs.AsNoTracking().SingleAsync(x => x.CompanyId == CompanyA);
        }

        await using (ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB)))
        {
            companyASong.Name = "Renamed by B";
            asB.Songs.Update(companyASong);

            await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asB.SaveChangesAsync());
        }

        await using (ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB)))
        {
            asB.Songs.Remove(companyASong);

            await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asB.SaveChangesAsync());
        }
    }

    [TestMethod]
    public async Task MovingARowToAnotherCompany_Throws()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedTwoCompaniesAsync(options);

        await using ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB));
        Song song = await asB.Songs.SingleAsync();
        song.CompanyId = CompanyA;

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asB.SaveChangesAsync());
    }
}
