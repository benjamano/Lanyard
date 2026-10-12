using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.ClientReleases;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.ClientReleases;

[TestClass]
public class ClientReleaseAdminServiceTests
{
    private const string PlatformAdminUserId = "platform-admin-user";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ClientReleaseAdminService GetService(DbContextOptions<ApplicationDbContext> options, bool isPlatformAdmin)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISystemDbContextFactory> systemFactory = new();
        systemFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IClientReleaseCatalogService> catalog = new();
        catalog.Setup(x => x.GetReleasesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                using ApplicationDbContext ctx = new(options);
                return Result<IReadOnlyList<ClientRelease>>.Ok(ctx.ClientReleases.AsNoTracking().ToList());
            });

        Mock<ITenantContext> tenant = new();
        tenant.SetupGet(x => x.IsPlatformAdmin).Returns(isPlatformAdmin);

        Mock<ISecurityService> security = new();
        security.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(PlatformAdminUserId));

        return new ClientReleaseAdminService(factory.Object, systemFactory.Object, catalog.Object, tenant.Object, security.Object,
            NullLogger<ClientReleaseAdminService>.Instance);
    }

    private static async Task SeedAsync(DbContextOptions<ApplicationDbContext> options, params ClientRelease[] releases)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.ClientReleases.AddRange(releases);
        await ctx.SaveChangesAsync();
    }

    private static async Task<ClientRelease> ReadAsync(DbContextOptions<ApplicationDbContext> options, string version)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ctx.ClientReleases.AsNoTracking().SingleAsync(x => x.Version == version);
    }

    [TestMethod]
    public async Task PromoteAsync_MakesACanaryReleaseStable()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, new ClientRelease { Version = "1.0.42", Tag = "t", IsPrerelease = true });

        Result<bool> result = await GetService(options, isPlatformAdmin: true).PromoteAsync("1.0.42");

        Assert.IsTrue(result.IsSuccess, result.Error);
        ClientRelease stored = await ReadAsync(options, "1.0.42");
        Assert.IsNotNull(stored.PromotedToStableAt);
        Assert.AreEqual(PlatformAdminUserId, stored.PromotedByUserId);
    }

    [TestMethod]
    public async Task EveryAction_IsRefused_ForACompanyAdmin()
    {
        // A release reaches every company's PCs, so a company Admin must not be able to move it.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, new ClientRelease { Version = "1.0.42", Tag = "t", IsPrerelease = true });
        ClientReleaseAdminService service = GetService(options, isPlatformAdmin: false);

        Assert.IsFalse((await service.PromoteAsync("1.0.42")).IsSuccess);
        Assert.IsFalse((await service.WithdrawAsync("1.0.42", "bad")).IsSuccess);
        Assert.IsFalse((await service.RestoreAsync("1.0.42")).IsSuccess);
        Assert.IsFalse((await service.GetOverviewAsync()).IsSuccess);

        ClientRelease stored = await ReadAsync(options, "1.0.42");
        Assert.IsNull(stored.PromotedToStableAt);
        Assert.IsNull(stored.WithdrawnAt);
    }

    [TestMethod]
    public async Task PromoteAsync_RefusesAWithdrawnRelease()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, new ClientRelease { Version = "1.0.42", Tag = "t", IsPrerelease = true, WithdrawnAt = DateTime.UtcNow });

        Result<bool> result = await GetService(options, isPlatformAdmin: true).PromoteAsync("1.0.42");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task WithdrawAsync_NeedsAReason()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, new ClientRelease { Version = "1.0.42", Tag = "t" });

        Result<bool> result = await GetService(options, isPlatformAdmin: true).WithdrawAsync("1.0.42", "   ");

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull((await ReadAsync(options, "1.0.42")).WithdrawnAt);
    }

    [TestMethod]
    public async Task WithdrawThenRestore_RoundTrips()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, new ClientRelease { Version = "1.0.42", Tag = "t" });
        ClientReleaseAdminService service = GetService(options, isPlatformAdmin: true);

        Result<bool> withdrawn = await service.WithdrawAsync("1.0.42", "  Projector windows don't open  ");
        ClientRelease afterWithdraw = await ReadAsync(options, "1.0.42");

        Result<bool> restored = await service.RestoreAsync("1.0.42");
        ClientRelease afterRestore = await ReadAsync(options, "1.0.42");

        Assert.IsTrue(withdrawn.IsSuccess, withdrawn.Error);
        Assert.AreEqual("Projector windows don't open", afterWithdraw.WithdrawnReason);
        Assert.AreEqual(PlatformAdminUserId, afterWithdraw.WithdrawnByUserId);
        Assert.IsTrue(restored.IsSuccess, restored.Error);
        Assert.IsNull(afterRestore.WithdrawnAt);
        Assert.IsNull(afterRestore.WithdrawnReason);
    }

    [TestMethod]
    public async Task GetOverviewAsync_CountsPcsPerVersionAcrossCompanies()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options,
            new ClientRelease { Version = "1.0.41", Tag = "a" },
            new ClientRelease { Version = "1.0.42", Tag = "b", IsPrerelease = true });

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.AddRange(
                new Client { Id = Guid.NewGuid(), Name = "A1", CompanyId = 1, ReportedClientVersion = "1.0.41" },
                new Client { Id = Guid.NewGuid(), Name = "B1", CompanyId = 2, ReportedClientVersion = "1.0.41" },
                new Client { Id = Guid.NewGuid(), Name = "A2", CompanyId = 1, ReportedClientVersion = "1.0.42", UpdateRing = ClientUpdateRing.Canary },
                new Client { Id = Guid.NewGuid(), Name = "Old", CompanyId = 2 });
            await ctx.SaveChangesAsync();
        }

        Result<IReadOnlyList<ClientReleaseOverviewDTO>> result = await GetService(options, isPlatformAdmin: true).GetOverviewAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.Single(x => x.Release.Version == "1.0.41").ReportedPcCount);
        Assert.AreEqual(1, result.Data!.Single(x => x.Release.Version == "1.0.42").ReportedPcCount);
        Assert.IsTrue(result.Data!.Single(x => x.Release.Version == "1.0.41").IsStable);
        Assert.IsFalse(result.Data!.Single(x => x.Release.Version == "1.0.42").IsStable);
    }
}
