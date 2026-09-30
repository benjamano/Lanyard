using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class StaffBulkUpdateServiceTests
{
    private static StaffBulkUpdateService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<StaffBulkUpdateService>.Instance);

    private static async Task GivePositionAsync(DbContextOptions<ApplicationDbContext> options, string userId, Guid positionId, bool isPrimary)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = userId, StaffPositionId = positionId, IsPrimary = isPrimary, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private static async Task<List<UserPosition>> PositionsOfAsync(DbContextOptions<ApplicationDbContext> options, string userId)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ctx.UserPositions.Where(x => x.UserId == userId).ToListAsync();
    }

    private static async Task SeedContractAsync(DbContextOptions<ApplicationDbContext> options, int companyId, string? userId,
        int? minShifts = null, decimal? minHours = null, decimal? maxHours = null)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.ContractRequirements.Add(new ContractRequirement
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            UserId = userId,
            MinShiftsPerWeek = minShifts,
            MinHoursPerWeek = minHours,
            MaxHoursPerWeek = maxHours,
            UpdateDate = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<TimeOffType> SeedTypeAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name)
    {
        await using ApplicationDbContext ctx = new(options);
        TimeOffType type = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = name, DeductsFromAllowance = true, IsActive = true };
        ctx.TimeOffTypes.Add(type);
        await ctx.SaveChangesAsync();
        return type;
    }

    private static BulkContractChange KeepAll() => new(
        BulkFieldChange<int>.Keep, BulkFieldChange<decimal>.Keep, BulkFieldChange<decimal>.Keep, BulkFieldChange<decimal>.Keep);

    [TestMethod]
    public async Task GetStaffAsync_ListsCompanyMembersWithPositionsAndContract()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Outsider");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");
        await GivePositionAsync(options, ben.Id, advisor.Id, isPrimary: true);
        await SeedContractAsync(options, company.Id, null, maxHours: 40m);
        await SeedContractAsync(options, company.Id, ben.Id, minHours: 16m);

        Result<List<StaffRotaSummary>> result = await GetService(options).GetStaffAsync(SchedulingTestHelpers.ManagerScopeFor(location), company.Id, location.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        StaffRotaSummary only = result.Data!.Single();
        Assert.AreEqual(ben.Id, only.UserId);
        Assert.AreEqual("Advisor", only.Positions.Single().Name);
        Assert.IsTrue(only.Positions.Single().IsPrimary);
        Assert.AreEqual(16m, only.Contract.MinHoursPerWeek.Value);
        Assert.AreEqual(40m, only.Contract.MaxHoursPerWeek.Value);
        Assert.IsTrue(only.HasContractOverride);
    }

    [TestMethod]
    public async Task GetStaffAsync_RefusesAManagerFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<List<StaffRotaSummary>> result = await GetService(options).GetStaffAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), company.Id, location.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ApplyPositionsAsync_AddKeepsExistingAndGivesFirstPrimaryOnlyToThoseWithout()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", 1);
        await GivePositionAsync(options, ben.Id, advisor.Id, isPrimary: true);

        Result<int> result = await GetService(options).ApplyPositionsAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id,
            [ben.Id, amy.Id], new BulkPositionChange(BulkPositionMode.Add, [supervisor.Id], null));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data);

        List<UserPosition> bens = await PositionsOfAsync(options, ben.Id);
        Assert.AreEqual(2, bens.Count);
        Assert.AreEqual(advisor.Id, bens.Single(x => x.IsPrimary).StaffPositionId);

        UserPosition amys = (await PositionsOfAsync(options, amy.Id)).Single();
        Assert.AreEqual(supervisor.Id, amys.StaffPositionId);
        Assert.IsTrue(amys.IsPrimary);
    }

    [TestMethod]
    public async Task ApplyPositionsAsync_AddWithPrimaryMovesEveryonesPrimary()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", 1);
        await GivePositionAsync(options, ben.Id, advisor.Id, isPrimary: true);

        Result<int> result = await GetService(options).ApplyPositionsAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id,
            [ben.Id], new BulkPositionChange(BulkPositionMode.Add, [supervisor.Id], supervisor.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        List<UserPosition> bens = await PositionsOfAsync(options, ben.Id);
        Assert.AreEqual(supervisor.Id, bens.Single(x => x.IsPrimary).StaffPositionId);
    }

    [TestMethod]
    public async Task ApplyPositionsAsync_RemovingPrimaryPromotesTheNextPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", 1);
        await GivePositionAsync(options, ben.Id, advisor.Id, isPrimary: true);
        await GivePositionAsync(options, ben.Id, supervisor.Id, isPrimary: false);

        Result<int> result = await GetService(options).ApplyPositionsAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id,
            [ben.Id, amy.Id], new BulkPositionChange(BulkPositionMode.Remove, [advisor.Id], null));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data, "Amy held nothing to remove, so only Ben changed.");
        UserPosition remaining = (await PositionsOfAsync(options, ben.Id)).Single();
        Assert.AreEqual(supervisor.Id, remaining.StaffPositionId);
        Assert.IsTrue(remaining.IsPrimary);
    }

    [TestMethod]
    public async Task ApplyPositionsAsync_ReplaceLeavesExactlyTheChosenPositions()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor", 1);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Party Host", 2);
        await GivePositionAsync(options, ben.Id, advisor.Id, isPrimary: true);

        Result<int> result = await GetService(options).ApplyPositionsAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id,
            [ben.Id], new BulkPositionChange(BulkPositionMode.Replace, [supervisor.Id, host.Id], host.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        List<UserPosition> bens = await PositionsOfAsync(options, ben.Id);
        CollectionAssert.AreEquivalent(new[] { supervisor.Id, host.Id }, bens.Select(x => x.StaffPositionId).ToArray());
        Assert.AreEqual(host.Id, bens.Single(x => x.IsPrimary).StaffPositionId);
    }

    [TestMethod]
    public async Task ApplyPositionsAsync_ChangesNobodyWhenOneSelectedPersonIsFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile outsider = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Outsider");
        StaffPosition advisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Advisor");

        Result<int> result = await GetService(options).ApplyPositionsAsync(SchedulingTestHelpers.ManagerScopeFor(location), company.Id, location.Id,
            [ben.Id, outsider.Id], new BulkPositionChange(BulkPositionMode.Add, [advisor.Id], null));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, (await PositionsOfAsync(options, ben.Id)).Count);
        Assert.AreEqual(0, (await PositionsOfAsync(options, outsider.Id)).Count);
    }

    [TestMethod]
    public async Task ApplyContractAsync_SetsChosenFieldsAndKeepsTheRest()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        await SeedContractAsync(options, company.Id, ben.Id, minShifts: 3, minHours: 10m);

        BulkContractChange change = KeepAll() with
        {
            MinHoursPerWeek = new BulkFieldChange<decimal>(BulkFieldMode.Set, 16m),
            MaxHoursPerWeek = new BulkFieldChange<decimal>(BulkFieldMode.Set, 30m)
        };

        Result<int> result = await GetService(options).ApplyContractAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id, [ben.Id, amy.Id], change, "manager");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data);

        await using ApplicationDbContext ctx = new(options);
        ContractRequirement bens = await ctx.ContractRequirements.SingleAsync(x => x.UserId == ben.Id);
        Assert.AreEqual(3, bens.MinShiftsPerWeek, "Left as is.");
        Assert.AreEqual(16m, bens.MinHoursPerWeek);
        Assert.AreEqual(30m, bens.MaxHoursPerWeek);

        ContractRequirement amys = await ctx.ContractRequirements.SingleAsync(x => x.UserId == amy.Id);
        Assert.IsNull(amys.MinShiftsPerWeek);
        Assert.AreEqual(16m, amys.MinHoursPerWeek);
        Assert.AreEqual("manager", amys.UpdatedByUserId);
    }

    [TestMethod]
    public async Task ApplyContractAsync_InheritingEveryValueRemovesThePersonalRow()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        await SeedContractAsync(options, company.Id, ben.Id, minShifts: 3);

        BulkContractChange change = KeepAll() with { MinShiftsPerWeek = new BulkFieldChange<int>(BulkFieldMode.Inherit) };

        Result<int> result = await GetService(options).ApplyContractAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id, [ben.Id], change, null);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse(await ctx.ContractRequirements.AnyAsync(x => x.UserId == ben.Id));
    }

    [TestMethod]
    public async Task ApplyContractAsync_RefusesEveryoneWhenAMinimumWouldExceedAnInheritedMaximum()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        await SeedContractAsync(options, company.Id, null, maxHours: 40m);
        await SeedContractAsync(options, company.Id, amy.Id, maxHours: 20m);

        BulkContractChange change = KeepAll() with { MinHoursPerWeek = new BulkFieldChange<decimal>(BulkFieldMode.Set, 30m) };

        Result<int> result = await GetService(options).ApplyContractAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id, [ben.Id, amy.Id], change, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Amy Tester");
        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse(await ctx.ContractRequirements.AnyAsync(x => x.UserId == ben.Id), "Ben was fine on his own but nobody changes.");
    }

    [TestMethod]
    public async Task ApplyAllowancesAsync_SetsInheritsAndLeavesUnlistedTypesAlone()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        TimeOffType holiday = await SeedTypeAsync(options, company, "Paid holiday");
        TimeOffType unpaid = await SeedTypeAsync(options, company, "Unpaid leave");
        TimeOffType training = await SeedTypeAsync(options, company, "Training days");

        await using (ApplicationDbContext seed = new(options))
        {
            seed.TimeOffAllowances.Add(new TimeOffAllowance { Id = Guid.NewGuid(), CompanyId = company.Id, TimeOffTypeId = unpaid.Id, UserId = ben.Id, AllowanceHours = 10m });
            seed.TimeOffAllowances.Add(new TimeOffAllowance { Id = Guid.NewGuid(), CompanyId = company.Id, TimeOffTypeId = training.Id, UserId = ben.Id, AllowanceHours = 5m });
            await seed.SaveChangesAsync();
        }

        Result<int> result = await GetService(options).ApplyAllowancesAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id, [ben.Id, amy.Id],
            [
                new BulkAllowanceChange(holiday.Id, BulkAllowanceMode.Amount, 224m),
                new BulkAllowanceChange(unpaid.Id, BulkAllowanceMode.Inherit)
            ],
            "manager");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data);

        await using ApplicationDbContext ctx = new(options);
        List<TimeOffAllowance> rows = await ctx.TimeOffAllowances.ToListAsync();
        Assert.AreEqual(224m, rows.Single(x => x.UserId == ben.Id && x.TimeOffTypeId == holiday.Id).AllowanceHours);
        Assert.AreEqual(224m, rows.Single(x => x.UserId == amy.Id && x.TimeOffTypeId == holiday.Id).AllowanceHours);
        Assert.IsFalse(rows.Any(x => x.TimeOffTypeId == unpaid.Id), "Inherit removes the personal row.");
        Assert.AreEqual(5m, rows.Single(x => x.TimeOffTypeId == training.Id).AllowanceHours, "Not in the change, so untouched.");
    }

    [TestMethod]
    public async Task ApplyAllowancesAsync_UnlimitedReplacesAnAmount()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        TimeOffType unpaid = await SeedTypeAsync(options, company, "Unpaid leave");

        Result<int> result = await GetService(options).ApplyAllowancesAsync(SchedulingTestHelpers.AdminScope, company.Id, location.Id, [ben.Id],
            [new BulkAllowanceChange(unpaid.Id, BulkAllowanceMode.Unlimited)], null);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsTrue((await ctx.TimeOffAllowances.SingleAsync()).IsUnlimited);
    }

    [TestMethod]
    public async Task ApplyAllowancesAsync_RefusesATypeFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        TimeOffType theirs = await SeedTypeAsync(options, other, "Paid holiday");

        Result<int> result = await GetService(options).ApplyAllowancesAsync(SchedulingTestHelpers.ManagerScopeFor(location), company.Id, location.Id, [ben.Id],
            [new BulkAllowanceChange(theirs.Id, BulkAllowanceMode.Amount, 8m)], null);

        Assert.IsFalse(result.IsSuccess);
    }

    private static async Task<Location> SeedSecondLocationAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name = "Wisbech")
    {
        await using ApplicationDbContext ctx = new(options);
        Location location = new() { CompanyId = company.Id, Name = name, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();
        return location;
    }

    [TestMethod]
    public async Task GetStaffAsync_ListsOnlyTheChosenLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await SeedSecondLocationAsync(options, company);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Ben");
        await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Amy");

        Result<List<StaffRotaSummary>> result = await GetService(options).GetStaffAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich), company.Id, ipswich.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ben.Id, result.Data!.Single().UserId);
    }

    [TestMethod]
    public async Task GetStaffAsync_RefusesAManagerLookingAtAnotherLocationOfTheirCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await SeedSecondLocationAsync(options, company);

        Result<List<StaffRotaSummary>> result = await GetService(options).GetStaffAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich), company.Id, wisbech.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetStaffAsync_LetsAnAdminPickAnyLocationOfTheCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await SeedSecondLocationAsync(options, company);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Amy");

        Result<List<StaffRotaSummary>> result = await GetService(options).GetStaffAsync(SchedulingTestHelpers.AdminScope, company.Id, wisbech.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(amy.Id, result.Data!.Single().UserId);
    }

    [TestMethod]
    public async Task ApplyContractAsync_ChangesNobodyWhenOneSelectedPersonIsAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await SeedSecondLocationAsync(options, company);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Ben");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Amy");

        BulkContractChange change = KeepAll() with { MaxHoursPerWeek = new BulkFieldChange<decimal>(BulkFieldMode.Set, 30m) };

        Result<int> result = await GetService(options).ApplyContractAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich), company.Id, ipswich.Id, [ben.Id, amy.Id], change, null);

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse(await ctx.ContractRequirements.AnyAsync());
    }
}
