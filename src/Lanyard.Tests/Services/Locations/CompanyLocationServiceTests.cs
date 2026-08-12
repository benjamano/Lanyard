using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

[TestClass]
public class CompanyLocationServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static CompanyLocationService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CompanyLocationService(factoryMock.Object);
    }

    private static async Task<(Company company, Location location)> SeedCompanyAndLocationAsync(
        DbContextOptions<ApplicationDbContext> options, string companyName = "Play2Day", string locationName = "Ipswich")
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = companyName, IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new() { CompanyId = company.Id, Name = locationName, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return (company, location);
    }

    private static async Task SeedUserAsync(DbContextOptions<ApplicationDbContext> options, string userId)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.Users.Add(new UserProfile { Id = userId, UserName = userId, FirstName = "Test", LastName = "User" });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task CompanyLocationService_SaveCompany_CreatesNewCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);

        Result<Company> result = await service.SaveCompanyAsync(new Company { Name = "Play2Day" });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(0, result.Data!.Id);

        await using ApplicationDbContext ctx = new(options);
        Company dbCompany = await ctx.Companies.SingleAsync(x => x.Id == result.Data.Id);
        Assert.AreEqual("Play2Day", dbCompany.Name);
        Assert.IsTrue(dbCompany.IsActive);
    }

    [TestMethod]
    public async Task CompanyLocationService_SaveLocation_CreatesLocationUnderCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (Company company, _) = await SeedCompanyAndLocationAsync(options, locationName: "Wisbech-seed");

        Result<Location> result = await service.SaveLocationAsync(new Location { CompanyId = company.Id, Name = "Ipswich" });

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Location dbLocation = await ctx.Locations.SingleAsync(x => x.Id == result.Data!.Id);
        Assert.AreEqual("Ipswich", dbLocation.Name);
        Assert.AreEqual(company.Id, dbLocation.CompanyId);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetLocations_FiltersByCompanyWhenProvided()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (Company companyA, _) = await SeedCompanyAndLocationAsync(options, "Play2Day", "Ipswich");
        (Company companyB, _) = await SeedCompanyAndLocationAsync(options, "Partyman", "Norwich");

        Result<List<Location>> result = await service.GetLocationsAsync(companyA.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual("Ipswich", result.Data![0].Name);
    }

    [TestMethod]
    public async Task CompanyLocationService_AddUserToLocation_CreatesMembership()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (_, Location location) = await SeedCompanyAndLocationAsync(options);
        await SeedUserAsync(options, "user-1");

        Result<bool> result = await service.AddUserToLocationAsync("user-1", location.Id);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        bool exists = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == "user-1" && x.LocationId == location.Id);
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public async Task CompanyLocationService_AddUserToLocation_FailsWhenAlreadyMember()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (_, Location location) = await SeedCompanyAndLocationAsync(options);
        await SeedUserAsync(options, "user-1");
        await service.AddUserToLocationAsync("user-1", location.Id);

        Result<bool> result = await service.AddUserToLocationAsync("user-1", location.Id);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("This user is already a member of that location.", result.Error);
    }

    [TestMethod]
    public async Task CompanyLocationService_RemoveUserFromLocation_DeletesMembership()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (_, Location location) = await SeedCompanyAndLocationAsync(options);
        await SeedUserAsync(options, "user-1");
        await service.AddUserToLocationAsync("user-1", location.Id);

        Result<bool> result = await service.RemoveUserFromLocationAsync("user-1", location.Id);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        bool exists = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == "user-1" && x.LocationId == location.Id);
        Assert.IsFalse(exists);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetLocationsForUser_ReturnsOnlyThatUsersLocations()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (Company company, Location ipswich) = await SeedCompanyAndLocationAsync(options, "Play2Day", "Ipswich");
        Location wisbech = (await service.SaveLocationAsync(new Location { CompanyId = company.Id, Name = "Wisbech" })).Data!;
        await SeedUserAsync(options, "user-1");
        await service.AddUserToLocationAsync("user-1", ipswich.Id);

        Result<List<Location>> result = await service.GetLocationsForUserAsync("user-1");

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual("Ipswich", result.Data![0].Name);
    }

    [TestMethod]
    public async Task CompanyLocationService_IsUserMemberOfLocation_ReturnsFalseWhenNotMember()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (_, Location location) = await SeedCompanyAndLocationAsync(options);
        await SeedUserAsync(options, "user-1");

        Result<bool> result = await service.IsUserMemberOfLocationAsync("user-1", location.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Data);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetLoginLocationOptions_ReturnsOnlyActiveLocationsWithDisplayName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (Company company, Location ipswich) = await SeedCompanyAndLocationAsync(options, "Play2Day", "Ipswich");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Locations.Add(new Location { CompanyId = company.Id, Name = "Closed Site", IsActive = false });
            await ctx.SaveChangesAsync();
        }

        await service.SaveCompanyAsync(new Company { Id = company.Id, Name = company.Name, ThemeColorHex = "#C8102E" });

        Result<List<LoginLocationOption>> result = await service.GetLoginLocationOptionsAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(ipswich.Id, result.Data![0].LocationId);
        Assert.AreEqual("Play2Day Ipswich", result.Data![0].DisplayName);
        Assert.AreEqual(company.Id, result.Data![0].CompanyId);
        Assert.AreEqual("#C8102E", result.Data[0].ThemeColorHex);
    }

    [TestMethod]
    public async Task CompanyLocationService_SaveCompany_RejectsMalformedHexColor()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);

        Result<Company> result = await service.SaveCompanyAsync(new Company { Name = "Play2Day", ThemeColorHex = "red" });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Theme color must be a hex value like #C8102E.", result.Error);
    }

    [TestMethod]
    public async Task CompanyLocationService_SaveCompany_AcceptsValidHexColorAndLogoFileId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        Guid logoFileId = Guid.NewGuid();

        Result<Company> result = await service.SaveCompanyAsync(new Company { Name = "Play2Day", ThemeColorHex = "#C8102E", LogoFileId = logoFileId });

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Company dbCompany = await ctx.Companies.SingleAsync(x => x.Id == result.Data!.Id);
        Assert.AreEqual("#C8102E", dbCompany.ThemeColorHex);
        Assert.AreEqual(logoFileId, dbCompany.LogoFileId);
    }

    [TestMethod]
    public async Task CompanyLocationService_SaveCompany_EmptyHexColorIsStoredAsNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);

        Result<Company> result = await service.SaveCompanyAsync(new Company { Name = "Play2Day", ThemeColorHex = "   " });

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Company dbCompany = await ctx.Companies.SingleAsync(x => x.Id == result.Data!.Id);
        Assert.IsNull(dbCompany.ThemeColorHex);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetCompanyBranding_ReturnsBrandingForActiveCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        Guid logoFileId = Guid.NewGuid();
        (Company company, _) = await SeedCompanyAndLocationAsync(options);
        await service.SaveCompanyAsync(new Company { Id = company.Id, Name = company.Name, ThemeColorHex = "#C8102E", LogoFileId = logoFileId });

        Result<CompanyBrandingInfo> result = await service.GetCompanyBrandingAsync(company.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("#C8102E", result.Data!.ThemeColorHex);
        Assert.AreEqual(logoFileId, result.Data.LogoFileId);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetCompanyBranding_FailsForMissingCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);

        Result<CompanyBrandingInfo> result = await service.GetCompanyBrandingAsync(999);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CompanyLocationService_GetCompanyBrandingForLocation_ResolvesThroughLocationToCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CompanyLocationService service = GetService(options);
        (Company company, Location location) = await SeedCompanyAndLocationAsync(options);
        await service.SaveCompanyAsync(new Company { Id = company.Id, Name = company.Name, ThemeColorHex = "#C8102E" });

        Result<CompanyBrandingInfo> result = await service.GetCompanyBrandingForLocationAsync(location.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(company.Id, result.Data!.CompanyId);
        Assert.AreEqual("#C8102E", result.Data.ThemeColorHex);
    }
}
