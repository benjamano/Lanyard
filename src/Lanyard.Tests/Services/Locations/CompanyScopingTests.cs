using Lanyard.Application.Services.Kitchen;
using Lanyard.Application.Services.Legal;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

/// <summary>
/// A Manager may administer their own company and nobody else's.
///
/// The Companies &amp; Locations page filters what it shows, but a filtered list is presentation,
/// not a boundary - these assert the services refuse the write regardless of what the page
/// displayed, which is what actually protects one tenant from another.
/// </summary>
[TestClass]
public class CompanyScopingTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Mock<IDbContextFactory<ApplicationDbContext>> Factory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> mock = new();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return mock;
    }

    private static async Task<(int Mine, int Theirs)> SeedTwoCompaniesAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company mine = new() { Name = "Play2Day", IsActive = true, ThemeColorHex = "#C32C28" };
        Company theirs = new() { Name = "Partyman", IsActive = true, ThemeColorHex = "#0044CC" };
        ctx.Companies.AddRange(mine, theirs);
        await ctx.SaveChangesAsync();

        return (mine.Id, theirs.Id);
    }

    [TestMethod]
    public async Task Manager_CanEditTheirOwnCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, _) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.ManagerOf(mine));

        await using ApplicationDbContext read = new(options);
        Company company = await read.Companies.AsNoTracking().FirstAsync(c => c.Id == mine);
        company.ThemeColorHex = "#123456";

        Result<Company> result = await service.SaveCompanyAsync(company);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task Manager_CannotEditAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, int theirs) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.ManagerOf(mine));

        await using ApplicationDbContext read = new(options);
        Company other = await read.Companies.AsNoTracking().FirstAsync(c => c.Id == theirs);
        other.ThemeColorHex = "#123456";

        Result<Company> result = await service.SaveCompanyAsync(other);

        Assert.IsFalse(result.IsSuccess);

        // And nothing was written.
        await using ApplicationDbContext check = new(options);
        Assert.AreEqual("#0044CC", (await check.Companies.FirstAsync(c => c.Id == theirs)).ThemeColorHex);
    }

    [TestMethod]
    public async Task Manager_CannotCreateANewCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, _) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.ManagerOf(mine));

        Result<Company> result = await service.SaveCompanyAsync(new Company { Name = "A new venture", IsActive = true });

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(2, await check.Companies.CountAsync());
    }

    /// <summary>
    /// Taking a whole tenant offline stays with admins, even for a manager's own company.
    /// </summary>
    [TestMethod]
    public async Task Manager_CannotDeactivateEvenTheirOwnCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, _) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.ManagerOf(mine));

        Result<bool> result = await service.DeactivateCompanyAsync(mine);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext check = new(options);
        Assert.IsTrue((await check.Companies.FirstAsync(c => c.Id == mine)).IsActive);
    }

    [TestMethod]
    public async Task Manager_CannotAddAVenueToAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, int theirs) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.ManagerOf(mine));

        Result<Location> result = await service.SaveLocationAsync(
            new Location { CompanyId = theirs, Name = "Sneaky venue", IsActive = true });

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(0, await check.Locations.CountAsync());
    }

    [TestMethod]
    public async Task Manager_CannotEditAnotherCompanysLegalDocuments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, int theirs) = await SeedTwoCompaniesAsync(options);

        CompanyLegalDocumentService service = new(
            Factory(options).Object,
            CompanyAccessMocks.ManagerOf(mine),
            new Mock<ILogger<CompanyLegalDocumentService>>().Object);

        Result<bool> theirDocument = await service.SaveAsync(
            theirs, LegalDocumentType.PrivacyPolicy, "<p>Not mine to write.</p>");

        Assert.IsFalse(theirDocument.IsSuccess);

        Result<bool> ownDocument = await service.SaveAsync(
            mine, LegalDocumentType.PrivacyPolicy, "<p>Mine to write.</p>");

        Assert.IsTrue(ownDocument.IsSuccess, ownDocument.Error);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(1, await check.CompanyLegalDocuments.CountAsync());
        Assert.AreEqual(mine, (await check.CompanyLegalDocuments.SingleAsync()).CompanyId);
    }

    /// <summary>
    /// A hostname decides which tenant a visitor sees, so claiming one for another company is
    /// the most damaging thing on this page and gets its own test.
    /// </summary>
    [TestMethod]
    public async Task Manager_CannotPointADomainAtAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, int theirs) = await SeedTwoCompaniesAsync(options);

        TenantDirectoryService service = new(
            Factory(options).Object,
            CompanyAccessMocks.ManagerOf(mine),
            new Mock<ILogger<TenantDirectoryService>>().Object);

        Result<CompanyDomain> result = await service.SaveDomainAsync(new CompanyDomain
        {
            CompanyId = theirs,
            Hostname = "partyman.co.uk",
            IsActive = true
        });

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(0, await check.CompanyDomains.CountAsync());
    }

    /// <summary>
    /// Signed out, or in a role with no company rights, nothing is editable. The page is
    /// role-gated as well, but a service that only refuses when someone else already refused is
    /// not a boundary.
    /// </summary>
    [TestMethod]
    public async Task NoAccess_CannotEditAnything()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int mine, _) = await SeedTwoCompaniesAsync(options);

        CompanyLocationService service = new(Factory(options).Object, CompanyAccessMocks.None());

        await using ApplicationDbContext read = new(options);
        Company company = await read.Companies.AsNoTracking().FirstAsync(c => c.Id == mine);
        company.ThemeColorHex = "#123456";

        Assert.IsFalse((await service.SaveCompanyAsync(company)).IsSuccess);
    }

    [TestMethod]
    public void CompanyAccess_AdminCanAdministerAnyCompanyIncludingOnesNotListed()
    {
        CompanyAccess admin = new(true, []);

        Assert.IsTrue(admin.CanAdminister(1));
        Assert.IsTrue(admin.CanAdminister(9999));
        Assert.IsTrue(admin.CanCreateCompanies);
    }

    [TestMethod]
    public void CompanyAccess_NoneGrantsNothing()
    {
        Assert.IsFalse(CompanyAccess.None.CanAdminister(1));
        Assert.IsFalse(CompanyAccess.None.CanCreateCompanies);
    }
}
