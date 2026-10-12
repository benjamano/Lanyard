using Lanyard.Application.Services.ClientReleases;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.ClientReleases;

[TestClass]
public class ClientUpdatePolicyServiceTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    private sealed class SignedInTenant(int? companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }

    private static readonly List<ClientRelease> Releases =
    [
        new() { Version = "1.0.40", Tag = "LanyardClient-1.0.40" },
        new() { Version = "1.0.41", Tag = "LanyardClient-1.0.41" },
        new() { Version = "1.0.42", Tag = "LanyardClient-1.0.42", IsPrerelease = true },
        new() { Version = "1.0.39", Tag = "LanyardClient-1.0.39", WithdrawnAt = DateTime.UtcNow }
    ];

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static (ClientUpdatePolicyService Service, Mock<IClientReleaseCatalogService> Catalog) GetService(
        DbContextOptions<ApplicationDbContext> options, ITenantProvider? tenant = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => tenant is null ? new ApplicationDbContext(options) : new ApplicationDbContext(options, tenant));

        Mock<IClientReleaseCatalogService> catalog = new();
        catalog.Setup(x => x.GetReleasesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ClientRelease>>.Ok(Releases));
        catalog.Setup(x => x.GetFeedForVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string version, CancellationToken _) => Result<ClientUpdateFeedResponse>.Ok(new ClientUpdateFeedResponse { TargetVersion = version }));

        return (new ClientUpdatePolicyService(factoryMock.Object, catalog.Object, NullLogger<ClientUpdatePolicyService>.Instance), catalog);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, int companyId = CompanyA,
        ClientUpdateRing ring = ClientUpdateRing.Stable, string? pinned = null, string? reported = null)
    {
        Guid id = Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);
        ctx.Clients.Add(new Client { Id = id, Name = "Kiosk", CompanyId = companyId, UpdateRing = ring, PinnedClientVersion = pinned, ReportedClientVersion = reported });
        await ctx.SaveChangesAsync();

        return id;
    }

    private static async Task<Client> ReadClientAsync(DbContextOptions<ApplicationDbContext> options, Guid id)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ctx.Clients.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [TestMethod]
    public async Task GetTargetAsync_ResolvesTheClientsRing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options, ring: ClientUpdateRing.Canary, reported: "1.0.41");

        Result<ClientUpdateTargetDTO> result = await GetService(options).Service.GetTargetAsync(id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("1.0.42", result.Data!.TargetVersion);
        Assert.IsTrue(result.Data.IsOffTarget);
    }

    [TestMethod]
    public async Task SetUpdateRingAsync_PinsToAKnownVersion()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options);

        Result<ClientUpdateTargetDTO> result = await GetService(options).Service.SetUpdateRingAsync(id, ClientUpdateRing.Pinned, "1.0.40");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("1.0.40", result.Data!.TargetVersion);

        Client stored = await ReadClientAsync(options, id);
        Assert.AreEqual(ClientUpdateRing.Pinned, stored.UpdateRing);
        Assert.AreEqual("1.0.40", stored.PinnedClientVersion);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("1.0.99")]
    public async Task SetUpdateRingAsync_RejectsAPinToAnUnknownVersion(string? pinned)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options);

        Result<ClientUpdateTargetDTO> result = await GetService(options).Service.SetUpdateRingAsync(id, ClientUpdateRing.Pinned, pinned);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientUpdateRing.Stable, (await ReadClientAsync(options, id)).UpdateRing);
    }

    [TestMethod]
    public async Task SetUpdateRingAsync_RejectsAPinToAWithdrawnVersion()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options);

        Result<ClientUpdateTargetDTO> result = await GetService(options).Service.SetUpdateRingAsync(id, ClientUpdateRing.Pinned, "1.0.39");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "withdrawn");
    }

    [TestMethod]
    public async Task SetUpdateRingAsync_ClearsThePin_WhenLeavingPinned()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options, ring: ClientUpdateRing.Pinned, pinned: "1.0.40");

        Result<ClientUpdateTargetDTO> result = await GetService(options).Service.SetUpdateRingAsync(id, ClientUpdateRing.Canary, "1.0.40");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Client stored = await ReadClientAsync(options, id);
        Assert.AreEqual(ClientUpdateRing.Canary, stored.UpdateRing);
        Assert.IsNull(stored.PinnedClientVersion);
    }

    [TestMethod]
    public async Task SetUpdateRingAsync_CannotChangeAnotherCompanysClient()
    {
        // Security: the kiosk ID is self-asserted, so pins must only be settable by an admin of
        // the company that owns the kiosk. The tenant filter makes the other company's kiosk invisible.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid companyBClient = await SeedClientAsync(options, companyId: CompanyB);

        Result<ClientUpdateTargetDTO> result = await GetService(options, new SignedInTenant(CompanyA)).Service
            .SetUpdateRingAsync(companyBClient, ClientUpdateRing.Pinned, "1.0.40");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientUpdateRing.Stable, (await ReadClientAsync(options, companyBClient)).UpdateRing);
    }

    [TestMethod]
    public async Task GetTargetsAsync_OnlyIncludesTheCallersCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid companyAClient = await SeedClientAsync(options, companyId: CompanyA);
        await SeedClientAsync(options, companyId: CompanyB);

        Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>> result = await GetService(options, new SignedInTenant(CompanyA)).Service.GetTargetsAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { companyAClient }, result.Data!.Keys.ToArray());
    }

    [TestMethod]
    public async Task GetFeedForClientAsync_ReturnsEmptyFeed_ForUnknownClient()
    {
        (ClientUpdatePolicyService service, Mock<IClientReleaseCatalogService> catalog) = GetService(GetInMemoryOptions());

        Result<ClientUpdateFeedResponse> result = await service.GetFeedForClientAsync(Guid.NewGuid());

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.TargetVersion);
        Assert.AreEqual(ClientUpdateFeedResponse.EmptyFeedJson, result.Data.FeedJson);
        catalog.Verify(x => x.GetFeedForVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetFeedForClientAsync_ReturnsEmptyFeed_WhenPinnedReleaseWasWithdrawn()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options, ring: ClientUpdateRing.Pinned, pinned: "1.0.39");

        Result<ClientUpdateFeedResponse> result = await GetService(options).Service.GetFeedForClientAsync(id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.TargetVersion);
    }

    [TestMethod]
    public async Task GetFeedForClientAsync_ReturnsTheTargetsFeed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid id = await SeedClientAsync(options);

        Result<ClientUpdateFeedResponse> result = await GetService(options).Service.GetFeedForClientAsync(id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("1.0.41", result.Data!.TargetVersion);
    }
}
