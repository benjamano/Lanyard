using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class StaffPositionServiceTests
{
    private static StaffPositionService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    [TestMethod]
    public async Task GetPositionsAsync_ReturnsOnlyActivePositionsForCompanyInSortOrder()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", sortOrder: 2);
        await SchedulingTestHelpers.SeedPositionAsync(options, company, "Customer Service Advisor", sortOrder: 1);
        await SchedulingTestHelpers.SeedPositionAsync(options, otherCompany, "Other Co Position");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.StaffPositions.Add(new StaffPosition { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Retired", IsActive = false });
            await ctx.SaveChangesAsync();
        }

        Result<List<StaffPosition>> result = await GetService(options).GetPositionsAsync(company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Data!.Count);
        Assert.AreEqual("Customer Service Advisor", result.Data[0].Name);
        Assert.AreEqual("Supervisor", result.Data[1].Name);
    }

    [TestMethod]
    public async Task SavePositionAsync_CreatesNewPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.AdminScope,
            new StaffPosition { CompanyId = company.Id, Name = "  Manager  ", ColorIndex = 3 });

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        StaffPosition saved = await ctx.StaffPositions.SingleAsync();
        Assert.AreEqual("Manager", saved.Name);
        Assert.AreEqual(3, saved.ColorIndex);
        Assert.IsTrue(saved.IsActive);
    }

    [TestMethod]
    public async Task SavePositionAsync_RejectsDuplicateNameInCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.AdminScope,
            new StaffPosition { CompanyId = company.Id, Name = "supervisor" });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "already exists");
    }

    [TestMethod]
    public async Task SavePositionAsync_AllowsSameNameInDifferentCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.AdminScope,
            new StaffPosition { CompanyId = otherCompany.Id, Name = "Supervisor" });

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task SavePositionAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        LocationScope otherCompanyManager = SchedulingTestHelpers.ManagerScopeFor(otherLocation);

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(otherCompanyManager,
            new StaffPosition { CompanyId = company.Id, Name = "Supervisor" });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SavePositionAsync_UpdatesExistingPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition position = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            new StaffPosition { Id = position.Id, CompanyId = company.Id, Name = "Shift Supervisor", Description = "Runs the floor", ColorIndex = 5, IsActive = true });

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        StaffPosition saved = await ctx.StaffPositions.SingleAsync(x => x.Id == position.Id);
        Assert.AreEqual("Shift Supervisor", saved.Name);
        Assert.AreEqual("Runs the floor", saved.Description);
        Assert.AreEqual(5, saved.ColorIndex);
    }

    [TestMethod]
    public async Task DeactivatePositionAsync_SoftDeletes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition position = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<bool> result = await GetService(options).DeactivatePositionAsync(SchedulingTestHelpers.AdminScope, position.Id);

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse((await ctx.StaffPositions.SingleAsync()).IsActive);
    }

    [TestMethod]
    public async Task DeactivatePositionAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        StaffPosition position = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<bool> result = await GetService(options).DeactivatePositionAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), position.Id);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsTrue((await ctx.StaffPositions.SingleAsync()).IsActive);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_FirstPositionBecomesPrimaryWhenNoneChosen()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.AdminScope, user.Id, [csa.Id, supervisor.Id], null);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Data!.Count);
        Assert.AreEqual(1, result.Data.Count(x => x.IsPrimary));
        Assert.AreEqual(csa.Id, result.Data.Single(x => x.IsPrimary).StaffPositionId);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_ReplacesSetAndMovesPrimary()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [csa.Id, supervisor.Id], csa.Id);

        Result<List<UserPosition>> result = await service.SetUserPositionsAsync(
            SchedulingTestHelpers.AdminScope, user.Id, [supervisor.Id, manager.Id], manager.Id);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEquivalent(new[] { supervisor.Id, manager.Id }, result.Data!.Select(x => x.StaffPositionId).ToArray());
        Assert.AreEqual(manager.Id, result.Data.Single(x => x.IsPrimary).StaffPositionId);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(2, await ctx.UserPositions.CountAsync(x => x.UserId == user.Id));
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_RejectsPrimaryNotInSet()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.AdminScope, user.Id, [csa.Id], supervisor.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_RejectsPositionsFromDifferentCompanies()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition foreign = await SchedulingTestHelpers.SeedPositionAsync(options, otherCompany, "Foreign");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.AdminScope, user.Id, [csa.Id, foreign.Id], null);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_RejectsManagerAssigningOtherCompanysPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.ManagerScopeFor(otherLocation), user.Id, [csa.Id], null);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_EmptySetClearsPositions()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [csa.Id], null);

        Result<List<UserPosition>> result = await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [], null);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetPrimaryPositionsForUsersAsync_MapsUsersWithoutPositionsToNull()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile withPosition = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile without = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, withPosition.Id, [csa.Id], csa.Id);

        Result<Dictionary<string, UserPosition?>> result = await service.GetPrimaryPositionsForUsersAsync([withPosition.Id, without.Id]);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(csa.Id, result.Data![withPosition.Id]!.StaffPositionId);
        Assert.AreEqual("CSA", result.Data[withPosition.Id]!.StaffPosition!.Name);
        Assert.IsNull(result.Data[without.Id]);
    }
    [TestMethod]
    public async Task SavePositionAsync_ReturnsSavedRowWithTrimmedName()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition position = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.AdminScope,
            new StaffPosition { Id = position.Id, CompanyId = company.Id, Name = "  Shift Lead  ", IsActive = true });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Shift Lead", result.Data!.Name);
    }

    [TestMethod]
    public async Task SavePositionAsync_ReactivatesInactivePositionWithSameName()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition retired = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        StaffPositionService service = GetService(options);
        await service.DeactivatePositionAsync(SchedulingTestHelpers.AdminScope, retired.Id);

        Result<StaffPosition> result = await service.SavePositionAsync(SchedulingTestHelpers.AdminScope,
            new StaffPosition { CompanyId = company.Id, Name = "supervisor", Description = "Back again", ColorIndex = 4 });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(retired.Id, result.Data!.Id);

        await using ApplicationDbContext ctx = new(options);
        StaffPosition saved = await ctx.StaffPositions.SingleAsync();
        Assert.IsTrue(saved.IsActive);
        Assert.AreEqual("supervisor", saved.Name);
        Assert.AreEqual("Back again", saved.Description);
        Assert.AreEqual(4, saved.ColorIndex);
    }

    [TestMethod]
    public async Task DeactivatePositionAsync_MovesPrimaryToAnotherActivePosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA", sortOrder: 1);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", sortOrder: 2);

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [csa.Id, supervisor.Id], supervisor.Id);

        Result<bool> result = await service.DeactivatePositionAsync(SchedulingTestHelpers.AdminScope, supervisor.Id);

        Assert.IsTrue(result.IsSuccess);

        Result<List<UserPosition>> positions = await service.GetUserPositionsAsync(user.Id);
        Assert.AreEqual(1, positions.Data!.Count);
        Assert.AreEqual(csa.Id, positions.Data[0].StaffPositionId);
        Assert.IsTrue(positions.Data[0].IsPrimary);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.UserPositions.CountAsync(x => x.UserId == user.Id && x.IsPrimary));
    }

    [TestMethod]
    public async Task DeactivatePositionAsync_ClearsPrimaryWhenUserHasNoOtherPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [supervisor.Id], supervisor.Id);

        await service.DeactivatePositionAsync(SchedulingTestHelpers.AdminScope, supervisor.Id);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.UserPositions.CountAsync(x => x.UserId == user.Id && x.IsPrimary));
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_EmptySetRejectedForManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        StaffPositionService service = GetService(options);
        await service.SetUserPositionsAsync(SchedulingTestHelpers.AdminScope, user.Id, [csa.Id], null);

        Result<List<UserPosition>> result = await service.SetUserPositionsAsync(
            SchedulingTestHelpers.ManagerScopeFor(otherLocation), user.Id, [], null);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.UserPositions.CountAsync(x => x.UserId == user.Id));
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_AllowsManagerOfUsersOwnCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), user.Id, [csa.Id], null);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
    }

    [TestMethod]
    public async Task SavePositionAsync_RejectsStaffWithoutManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<StaffPosition> result = await GetService(options).SavePositionAsync(SchedulingTestHelpers.StaffScopeFor(location),
            new StaffPosition { CompanyId = company.Id, Name = "Supervisor" });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetUserPositionsAsync_RejectsStaffWithoutManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        Result<List<UserPosition>> result = await GetService(options).SetUserPositionsAsync(
            SchedulingTestHelpers.StaffScopeFor(location), user.Id, [csa.Id], null);

        Assert.IsFalse(result.IsSuccess);
    }
}
