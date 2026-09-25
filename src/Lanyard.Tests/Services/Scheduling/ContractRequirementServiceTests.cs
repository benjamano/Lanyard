using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ContractRequirementServiceTests
{
    private static ContractRequirementService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    private static async Task SeedTierAsync(DbContextOptions<ApplicationDbContext> options, int companyId, Guid? positionId, string? userId,
        int? minShifts = null, decimal? minShiftLength = null, decimal? minHours = null, decimal? maxHours = null)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.ContractRequirements.Add(new ContractRequirement
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            StaffPositionId = positionId,
            UserId = userId,
            MinShiftsPerWeek = minShifts,
            MinShiftLengthHours = minShiftLength,
            MinHoursPerWeek = minHours,
            MaxHoursPerWeek = maxHours,
            UpdateDate = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync();
    }

    private static async Task MakePrimaryAsync(DbContextOptions<ApplicationDbContext> options, string userId, Guid positionId)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = userId, StaffPositionId = positionId, IsPrimary = true, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ResolveForUserAsync_ReturnsEmptyWhenNothingConfigured()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<ResolvedContract> result = await GetService(options).ResolveForUserAsync(user.Id, company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data!.HasAnyRequirement);
        Assert.AreEqual(ContractTier.None, result.Data.MinShiftsPerWeek.Source);
    }

    [TestMethod]
    public async Task ResolveForUserAsync_UsesCompanyDefaultWhenNoOverrides()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1, minShiftLength: 2);

        Result<ResolvedContract> result = await GetService(options).ResolveForUserAsync(user.Id, company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.MinShiftsPerWeek.Value);
        Assert.AreEqual(ContractTier.Company, result.Data.MinShiftsPerWeek.Source);
        Assert.AreEqual(2m, result.Data.MinShiftLengthHours.Value);
        Assert.IsNull(result.Data.MaxHoursPerWeek.Value);
    }

    [TestMethod]
    public async Task ResolveForUserAsync_PositionOverridesCompanyPerField()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await MakePrimaryAsync(options, user.Id, manager.Id);
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1, minShiftLength: 2);
        await SeedTierAsync(options, company.Id, manager.Id, null, minHours: 37.5m);

        Result<ResolvedContract> result = await GetService(options).ResolveForUserAsync(user.Id, company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.MinShiftsPerWeek.Value);
        Assert.AreEqual(ContractTier.Company, result.Data.MinShiftsPerWeek.Source);
        Assert.AreEqual(37.5m, result.Data.MinHoursPerWeek.Value);
        Assert.AreEqual(ContractTier.Position, result.Data.MinHoursPerWeek.Source);
    }

    [TestMethod]
    public async Task ResolveForUserAsync_UserOverridesPositionPerField()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await MakePrimaryAsync(options, user.Id, manager.Id);
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1, minShiftLength: 2, maxHours: 48);
        await SeedTierAsync(options, company.Id, manager.Id, null, minHours: 37.5m);
        await SeedTierAsync(options, company.Id, null, user.Id, minHours: 20, minShifts: 3);

        Result<ResolvedContract> result = await GetService(options).ResolveForUserAsync(user.Id, company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(3, result.Data!.MinShiftsPerWeek.Value);
        Assert.AreEqual(ContractTier.User, result.Data.MinShiftsPerWeek.Source);
        Assert.AreEqual(20m, result.Data.MinHoursPerWeek.Value);
        Assert.AreEqual(ContractTier.User, result.Data.MinHoursPerWeek.Source);
        Assert.AreEqual(2m, result.Data.MinShiftLengthHours.Value);
        Assert.AreEqual(ContractTier.Company, result.Data.MinShiftLengthHours.Source);
        Assert.AreEqual(48m, result.Data.MaxHoursPerWeek.Value);
    }

    [TestMethod]
    public async Task ResolveForUserAsync_IgnoresNonPrimaryPositionOverride()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");
        await MakePrimaryAsync(options, user.Id, csa.Id);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = supervisor.Id, IsPrimary = false, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await SeedTierAsync(options, company.Id, supervisor.Id, null, maxHours: 10);

        Result<ResolvedContract> result = await GetService(options).ResolveForUserAsync(user.Id, company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data!.MaxHoursPerWeek.Value);
    }

    [TestMethod]
    public async Task ResolveForUsersAsync_ResolvesEachUserIndependently()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1);
        await SeedTierAsync(options, company.Id, null, bob.Id, minShifts: 4);

        Result<Dictionary<string, ResolvedContract>> result = await GetService(options).ResolveForUsersAsync([ann.Id, bob.Id], company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data![ann.Id].MinShiftsPerWeek.Value);
        Assert.AreEqual(4, result.Data[bob.Id].MinShiftsPerWeek.Value);
    }

    [TestMethod]
    public async Task SaveTierAsync_CreatesCompanyDefault()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, MinShiftsPerWeek = 1, MinShiftLengthHours = 2 });

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Data);

        await using ApplicationDbContext ctx = new(options);
        ContractRequirement saved = await ctx.ContractRequirements.SingleAsync();
        Assert.IsNull(saved.StaffPositionId);
        Assert.IsNull(saved.UserId);
        Assert.AreEqual(1, saved.MinShiftsPerWeek);
    }

    [TestMethod]
    public async Task SaveTierAsync_UpdatesExistingRowInsteadOfDuplicating()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, MinShiftsPerWeek = 2 });

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.ContractRequirements.CountAsync());
        Assert.AreEqual(2, (await ctx.ContractRequirements.SingleAsync()).MinShiftsPerWeek);
    }

    [TestMethod]
    public async Task SaveTierAsync_DeletesOverrideRowWhenAllFieldsNull()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await SeedTierAsync(options, company.Id, manager.Id, null, minHours: 37.5m);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, StaffPositionId = manager.Id });

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.ContractRequirements.CountAsync());
    }

    [TestMethod]
    public async Task SaveTierAsync_RejectsPositionFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        StaffPosition foreign = await SchedulingTestHelpers.SeedPositionAsync(options, otherCompany, "Foreign");

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, StaffPositionId = foreign.Id, MinShiftsPerWeek = 1 });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTierAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation),
            new ContractRequirement { CompanyId = company.Id, MinShiftsPerWeek = 1 });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTierAsync_RejectsMinHoursAboveMax()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, MinHoursPerWeek = 40, MaxHoursPerWeek = 20 });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetTierAsync_ReturnsOnlyTheRequestedTier()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1);
        await SeedTierAsync(options, company.Id, manager.Id, null, minShifts: 2);
        await SeedTierAsync(options, company.Id, null, user.Id, minShifts: 3);

        ContractRequirementService service = GetService(options);

        Result<ContractRequirement?> companyTier = await service.GetTierAsync(company.Id, null, null);
        Result<ContractRequirement?> positionTier = await service.GetTierAsync(company.Id, manager.Id, null);
        Result<ContractRequirement?> userTier = await service.GetTierAsync(company.Id, null, user.Id);

        Assert.AreEqual(1, companyTier.Data!.MinShiftsPerWeek);
        Assert.AreEqual(2, positionTier.Data!.MinShiftsPerWeek);
        Assert.AreEqual(3, userTier.Data!.MinShiftsPerWeek);
    }
    [TestMethod]
    public async Task SaveTierAsync_RejectsUserMinimumAboveInheritedMaximum()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedTierAsync(options, company.Id, null, null, maxHours: 40);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, UserId = user.Id, MinHoursPerWeek = 50 });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "inherited from the company default");
    }

    [TestMethod]
    public async Task SaveTierAsync_RejectsPositionMaximumBelowInheritedMinimum()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await SeedTierAsync(options, company.Id, null, null, minHours: 30);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, StaffPositionId = manager.Id, MaxHoursPerWeek = 20 });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTierAsync_UserTierValidatesAgainstPrimaryPositionNotCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition manager = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        await MakePrimaryAsync(options, user.Id, manager.Id);
        await SeedTierAsync(options, company.Id, null, null, maxHours: 40);
        await SeedTierAsync(options, company.Id, manager.Id, null, maxHours: 60);

        // 50 h exceeds the company's 40 h but not the primary position's 60 h, which is what applies.
        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, UserId = user.Id, MinHoursPerWeek = 50 });

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTierAsync_AllowsOverrideThatRaisesBothBounds()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedTierAsync(options, company.Id, null, null, minHours: 10, maxHours: 40);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.AdminScope,
            new ContractRequirement { CompanyId = company.Id, UserId = user.Id, MinHoursPerWeek = 45, MaxHoursPerWeek = 50 });

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task ResolveForUsersAsync_ToleratesDuplicatePrimaryRows()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        // The partial unique index prevents this in Postgres; EF InMemory doesn't enforce it, which
        // is exactly what lets the test check the read side copes rather than throwing.
        await MakePrimaryAsync(options, user.Id, csa.Id);
        await MakePrimaryAsync(options, user.Id, supervisor.Id);
        await SeedTierAsync(options, company.Id, null, null, minShifts: 1);

        Result<Dictionary<string, ResolvedContract>> result = await GetService(options).ResolveForUsersAsync([user.Id], company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data![user.Id].MinShiftsPerWeek.Value);
    }

    [TestMethod]
    public async Task SaveTierAsync_RejectsStaffWithoutManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<ContractRequirement?> result = await GetService(options).SaveTierAsync(SchedulingTestHelpers.StaffScopeFor(location),
            new ContractRequirement { CompanyId = company.Id, MinShiftsPerWeek = 1 });

        Assert.IsFalse(result.IsSuccess);
    }
}
