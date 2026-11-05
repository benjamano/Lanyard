using Lanyard.Application.Services;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

/// <summary>
/// The per-company kiosk list the Clients page, Music page and Kiosk Health widget share. The
/// cache is process-wide, so most of what matters here is that one company never sees another's
/// kiosks and that a change shows up as soon as it's published.
/// </summary>
[TestClass]
public class KioskClientListServiceTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    private static readonly DateTime Start = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Tenant(bool isSystem, int? companyId) : ITenantProvider
    {
        public bool IsSystem => isSystem;
        public int? CompanyId => companyId;
    }

    private static Tenant SignedIn(int? companyId) => new(false, companyId);

    private sealed class TestableKioskClientListService(
        IClientService clientService,
        IKioskClientListCache cache,
        ITenantProvider tenant,
        Func<IReadOnlyCollection<string>> connectedIds)
        : KioskClientListService(clientService, cache, tenant)
    {
        protected override IReadOnlyCollection<string> GetConnectedConnectionIds() => connectedIds();
    }

    // Everything one test needs: a shared database, bus and cache, as in the running app, and a
    // way to make a list service for any caller.
    private sealed class Fixture
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public KioskFleetEventBus Bus { get; } = new();
        public TestClock Clock { get; } = new(Start);
        public KioskClientListCache Cache { get; }
        public List<string> ConnectedIds { get; } = [];

        public Fixture()
        {
            Cache = new KioskClientListCache(Bus, Clock);
        }

        public KioskClientListService ServiceFor(ITenantProvider tenant)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(Options, tenant));

            ClientService clientService = new(
                factoryMock.Object,
                Mock.Of<IHubContext<SignalRControlHub>>(),
                NullLogger<ClientService>.Instance,
                new MemoryCache(new MemoryCacheOptions()),
                Bus);

            return new TestableKioskClientListService(clientService, Cache, tenant, () => ConnectedIds.ToList());
        }

        public async Task<Guid> SeedAsync(int companyId, string name, string? connectionId = null)
        {
            await using ApplicationDbContext ctx = new(Options);

            Client client = new() { Id = Guid.NewGuid(), Name = name, CompanyId = companyId, MostRecentConnectionId = connectionId };
            ctx.Clients.Add(client);
            await ctx.SaveChangesAsync();

            return client.Id;
        }

        // Straight into the database, the way another server write would land without going
        // through anything that publishes.
        public async Task RenameQuietlyAsync(Guid id, string name)
        {
            await using ApplicationDbContext ctx = new(Options);

            Client client = await ctx.Clients.SingleAsync(x => x.Id == id);
            client.Name = name;
            await ctx.SaveChangesAsync();
        }
    }

    private static async Task<List<string?>> NamesAsync(KioskClientListService service, bool forceRefresh = false)
    {
        Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> result = await service.GetClientsAsync(forceRefresh);

        Assert.IsTrue(result.IsSuccess, result.Error);

        return result.Data!.Select(x => x.Name).OrderBy(x => x).ToList();
    }

    [TestMethod]
    public async Task GetClients_WithinTheTtl_IsServedFromTheCache()
    {
        Fixture fixture = new();
        Guid id = await fixture.SeedAsync(CompanyA, "Arena");
        KioskClientListService service = fixture.ServiceFor(SignedIn(CompanyA));

        await NamesAsync(service);
        await fixture.RenameQuietlyAsync(id, "Renamed");

        CollectionAssert.AreEqual(new[] { "Arena" }, await NamesAsync(service), "Second read came from the cache.");

        fixture.Clock.Advance(KioskClientListCache.Ttl);

        CollectionAssert.AreEqual(new[] { "Renamed" }, await NamesAsync(service), "Expired entries are read again.");
    }

    [TestMethod]
    public async Task GetClients_IsSharedBetweenTabsOfTheSameCompany()
    {
        Fixture fixture = new();
        Guid id = await fixture.SeedAsync(CompanyA, "Arena");

        await NamesAsync(fixture.ServiceFor(SignedIn(CompanyA)));
        await fixture.RenameQuietlyAsync(id, "Renamed");

        CollectionAssert.AreEqual(new[] { "Arena" }, await NamesAsync(fixture.ServiceFor(SignedIn(CompanyA))));
    }

    [TestMethod]
    public async Task GetClients_NeverShowsAnotherCompanysKiosks()
    {
        Fixture fixture = new();
        await fixture.SeedAsync(CompanyA, "Arena");
        await fixture.SeedAsync(CompanyB, "Bowling");

        CollectionAssert.AreEqual(new[] { "Arena" }, await NamesAsync(fixture.ServiceFor(SignedIn(CompanyA))));
        CollectionAssert.AreEqual(new[] { "Bowling" }, await NamesAsync(fixture.ServiceFor(SignedIn(CompanyB))));
        CollectionAssert.AreEqual(new[] { "Arena" }, await NamesAsync(fixture.ServiceFor(SignedIn(CompanyA))), "Company A's warm entry is still its own.");
    }

    [TestMethod]
    public async Task Publishing_DropsThatCompanysEntry_Only()
    {
        Fixture fixture = new();
        Guid arena = await fixture.SeedAsync(CompanyA, "Arena");
        Guid bowling = await fixture.SeedAsync(CompanyB, "Bowling");
        KioskClientListService companyA = fixture.ServiceFor(SignedIn(CompanyA));
        KioskClientListService companyB = fixture.ServiceFor(SignedIn(CompanyB));

        await NamesAsync(companyA);
        await NamesAsync(companyB);
        await fixture.RenameQuietlyAsync(arena, "Arena 2");
        await fixture.RenameQuietlyAsync(bowling, "Bowling 2");

        fixture.Bus.Publish(CompanyA);

        CollectionAssert.AreEqual(new[] { "Arena 2" }, await NamesAsync(companyA));
        CollectionAssert.AreEqual(new[] { "Bowling" }, await NamesAsync(companyB));
    }

    [TestMethod]
    public async Task A_WriteThroughClientService_ShowsStraightAway()
    {
        Fixture fixture = new();
        Guid id = await fixture.SeedAsync(CompanyA, "Arena");
        KioskClientListService service = fixture.ServiceFor(SignedIn(CompanyA));

        await NamesAsync(service);

        Client client;
        await using (ApplicationDbContext ctx = new(fixture.Options))
        {
            client = await ctx.Clients.AsNoTracking().SingleAsync(x => x.Id == id);
        }

        client.Name = "Renamed in settings";

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(fixture.Options));
        await new ClientService(factoryMock.Object, Mock.Of<IHubContext<SignalRControlHub>>(), NullLogger<ClientService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), fixture.Bus).UpdateClientAsync(client);

        CollectionAssert.AreEqual(new[] { "Renamed in settings" }, await NamesAsync(service));
    }

    [TestMethod]
    public async Task ForceRefresh_ReadsTheDatabaseAgain()
    {
        Fixture fixture = new();
        Guid id = await fixture.SeedAsync(CompanyA, "Arena");
        KioskClientListService service = fixture.ServiceFor(SignedIn(CompanyA));

        await NamesAsync(service);
        await fixture.RenameQuietlyAsync(id, "Renamed");

        CollectionAssert.AreEqual(new[] { "Renamed" }, await NamesAsync(service, forceRefresh: true));
        CollectionAssert.AreEqual(new[] { "Renamed" }, await NamesAsync(service), "The forced read refilled the cache.");
    }

    [TestMethod]
    public async Task SystemAndUnresolvedCallers_AreNeverCached()
    {
        Fixture fixture = new();
        Guid id = await fixture.SeedAsync(CompanyA, "Arena");
        await fixture.SeedAsync(CompanyB, "Bowling");

        KioskClientListService system = fixture.ServiceFor(new Tenant(true, null));
        KioskClientListService unresolved = fixture.ServiceFor(SignedIn(null));

        CollectionAssert.AreEqual(new[] { "Arena", "Bowling" }, await NamesAsync(system));
        await fixture.RenameQuietlyAsync(id, "Renamed");
        CollectionAssert.AreEqual(new[] { "Bowling", "Renamed" }, await NamesAsync(system));

        CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync(unresolved), "Fails closed, as the tenant filter does.");

        CollectionAssert.AreEqual(new[] { "Renamed" }, await NamesAsync(fixture.ServiceFor(SignedIn(CompanyA))),
            "Neither left anything in company A's entry.");
    }

    [TestMethod]
    public async Task ConnectedFlag_IsLiveEvenOnACacheHit()
    {
        Fixture fixture = new();
        await fixture.SeedAsync(CompanyA, "Arena", connectionId: "conn-1");
        KioskClientListService service = fixture.ServiceFor(SignedIn(CompanyA));

        Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> before = await service.GetClientsAsync();

        fixture.ConnectedIds.Add("conn-1");

        Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> after = await service.GetClientsAsync();

        Assert.IsFalse(before.Data!.Single().IsCurrentlyConnected);
        Assert.IsTrue(after.Data!.Single().IsCurrentlyConnected);
    }

    [TestMethod]
    public async Task EachCaller_GetsItsOwnCopies()
    {
        Fixture fixture = new();
        await fixture.SeedAsync(CompanyA, "Arena");
        KioskClientListService service = fixture.ServiceFor(SignedIn(CompanyA));

        Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> first = await service.GetClientsAsync();
        first.Data!.Single().Name = "Changed by one page";

        Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> second = await service.GetClientsAsync();

        Assert.AreNotSame(first.Data!.Single(), second.Data!.Single());
        Assert.AreEqual("Arena", second.Data!.Single().Name);
    }

    [TestMethod]
    public void TrySet_FromALoadThatStartedBeforeAnEviction_IsIgnored()
    {
        KioskFleetEventBus bus = new();
        KioskClientListCache cache = new(bus, new TestClock(Start));

        long version = cache.GetVersion(CompanyA);
        bus.Publish(CompanyA);
        cache.TrySet(CompanyA, version, [new ClientConnectedWithCapabilitiesDTO { Name = "Stale" }]);

        Assert.IsFalse(cache.TryGet(CompanyA, out _));
    }
}
