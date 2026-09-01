using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

[TestClass]
public class TenantDirectoryServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static TenantDirectoryService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new TenantDirectoryService(factoryMock.Object, new Mock<ILogger<TenantDirectoryService>>().Object);
    }

    private static async Task<int> SeedCompanyAsync(
        DbContextOptions<ApplicationDbContext> options,
        string name,
        string? hostname = null,
        string? slug = null,
        string? themeColorHex = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new()
        {
            Name = name,
            Slug = slug,
            ThemeColorHex = themeColorHex,
            IsActive = true
        };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        if (hostname is not null)
        {
            ctx.CompanyDomains.Add(new CompanyDomain
            {
                CompanyId = company.Id,
                Hostname = hostname,
                IsPrimary = true,
                IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        return company.Id;
    }

    [TestMethod]
    public async Task GetTenantByHostnameAsync_ResolvesTheOwningCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("play2day.co.uk");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(companyId, result.Data!.CompanyId);
        Assert.AreEqual("play2day.co.uk", result.Data.PrimaryHost);
    }

    [TestMethod]
    public async Task GetTenantByHostnameAsync_IsCaseInsensitiveAndIgnoresPort()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");

        TenantDirectoryService service = GetService(options);

        Assert.IsTrue((await service.GetTenantByHostnameAsync("Play2Day.CO.UK")).Success);
        Assert.IsTrue((await service.GetTenantByHostnameAsync("play2day.co.uk:8080")).Success);
    }

    /// <summary>
    /// An unrecognised host must resolve to nothing rather than to a default tenant - serving
    /// one customer's branding on another's domain would be worse than serving an error.
    /// </summary>
    [TestMethod]
    public async Task GetTenantByHostnameAsync_FailsForAnUnknownHost()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("someone-elses-domain.com");

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// www and apex are separate rows so a customer can point one at us before the other;
    /// folding them together here would resolve a host nobody configured.
    /// </summary>
    [TestMethod]
    public async Task GetTenantByHostnameAsync_DoesNotSilentlyFoldWwwOntoApex()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("www.play2day.co.uk");

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task GetTenantBySlugAsync_ResolvesBeforeDnsIsPointed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "Partyman", slug: "partyman");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantBySlugAsync("partyman");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(companyId, result.Data!.CompanyId);
    }

    [TestMethod]
    public async Task GetTenantByHostnameAsync_FallsBackToTheDefaultBrandColour()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("play2day.co.uk");

        Assert.AreEqual(BrandConstants.PrimaryColorHex, result.Data!.ThemeColorHex);
    }

    /// <summary>
    /// A tenant picking a pale brand colour must not end up with unreadable white-on-yellow.
    /// </summary>
    [TestMethod]
    public async Task GetTenantByHostnameAsync_ChoosesAReadableForegroundForAPaleBrandColour()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Bright", hostname: "bright.example", themeColorHex: "#ffee00");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("bright.example");

        Assert.AreEqual(BrandConstants.OnLightTextHex, result.Data!.OnPrimaryColorHex);
    }

    [TestMethod]
    public async Task GetTenantByHostnameAsync_ChoosesAReadableForegroundForADarkBrandColour()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Deep", hostname: "deep.example", themeColorHex: "#101a3c");

        Result<TenantBrandingDto> result = await GetService(options).GetTenantByHostnameAsync("deep.example");

        Assert.AreEqual(BrandConstants.OnDarkTextHex, result.Data!.OnPrimaryColorHex);
    }

    [TestMethod]
    public async Task SaveDomainAsync_RejectsAHostAlreadyClaimedByAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");
        int partymanId = await SeedCompanyAsync(options, "Partyman");

        Result<CompanyDomain> result = await GetService(options).SaveDomainAsync(new CompanyDomain
        {
            CompanyId = partymanId,
            Hostname = "play2day.co.uk"
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "another company");
    }

    [TestMethod]
    public async Task SaveDomainAsync_RejectsAUrlRatherThanAHostname()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "Play2Day");

        Result<CompanyDomain> result = await GetService(options).SaveDomainAsync(new CompanyDomain
        {
            CompanyId = companyId,
            Hostname = "https://play2day.co.uk/order"
        });

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// Exactly one primary per company: printed QR codes are built from it, and "whichever row
    /// came back first" is not a stable basis for something stuck to a table.
    /// </summary>
    [TestMethod]
    public async Task SaveDomainAsync_DemotesThePreviousPrimaryWhenANewOneIsSet()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "Play2Day", hostname: "play2day.co.uk");
        TenantDirectoryService service = GetService(options);

        await service.SaveDomainAsync(new CompanyDomain
        {
            CompanyId = companyId,
            Hostname = "www.play2day.co.uk",
            IsPrimary = true
        });

        Result<List<CompanyDomain>> domains = await service.GetDomainsForCompanyAsync(companyId);

        Assert.AreEqual(1, domains.Data!.Count(d => d.IsPrimary));
        Assert.AreEqual("www.play2day.co.uk", domains.Data.Single(d => d.IsPrimary).Hostname);
    }
}
