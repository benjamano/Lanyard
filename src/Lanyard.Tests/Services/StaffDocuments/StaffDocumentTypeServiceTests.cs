using Lanyard.Application.Services.StaffDocuments;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.StaffDocuments;

[TestClass]
public class StaffDocumentTypeServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static StaffDocumentTypeService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new StaffDocumentTypeService(factoryMock.Object);
    }

    private static async Task<Company> SeedCompanyAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company;
    }

    [TestMethod]
    public async Task GetDocumentTypesAsync_ReturnsOnlyActiveTypesForCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Company otherCompany = new() { Name = "Other Co", IsActive = true };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Companies.Add(otherCompany);
            ctx.StaffDocumentTypes.AddRange(
                new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "DBS Check", IsActive = true },
                new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Old Type", IsActive = false },
                new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = otherCompany.Id, Name = "Other Company's Type", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        StaffDocumentTypeService service = GetService(options);
        Result<List<StaffDocumentType>> result = await service.GetDocumentTypesAsync(company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual("DBS Check", result.Data![0].Name);
    }

    [TestMethod]
    public async Task SaveDocumentTypeAsync_RequiresName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);

        StaffDocumentTypeService service = GetService(options);

        Result<StaffDocumentType> result = await service.SaveDocumentTypeAsync(new StaffDocumentType
        {
            CompanyId = company.Id,
            Name = "  ",
            RequiresExpiryDate = true
        });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveDocumentTypeAsync_CreatesNewType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);

        StaffDocumentTypeService service = GetService(options);

        Result<StaffDocumentType> result = await service.SaveDocumentTypeAsync(new StaffDocumentType
        {
            CompanyId = company.Id,
            Name = "First Aid Certificate",
            RequiresExpiryDate = true
        });

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.StaffDocumentTypes.CountAsync(x => x.CompanyId == company.Id));
    }

    [TestMethod]
    public async Task DeactivateDocumentTypeAsync_SetsIsActiveFalse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Guid typeId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.StaffDocumentTypes.Add(new StaffDocumentType { Id = typeId, CompanyId = company.Id, Name = "DBS Check", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        StaffDocumentTypeService service = GetService(options);
        Result<bool> result = await service.DeactivateDocumentTypeAsync(typeId);

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext verifyCtx = new(options);
        StaffDocumentType type = await verifyCtx.StaffDocumentTypes.FirstAsync(x => x.Id == typeId);
        Assert.IsFalse(type.IsActive);
    }
}
