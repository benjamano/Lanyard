using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class TimeOffPolicyServiceTests
{
    private static TimeOffPolicyService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<TimeOffPolicyService>.Instance);

    private static async Task<TimeOffType> TypeNamedAsync(TimeOffPolicyService service, Company company, string name)
    {
        Result<List<TimeOffType>> types = await service.GetTypesAsync(company.Id);
        return types.Data!.Single(x => x.Name == name);
    }

    private static TimeOffAllowance Row(Company company, TimeOffType type, decimal hours = 0, bool unlimited = false, Guid? positionId = null, string? userId = null) => new()
    {
        CompanyId = company.Id,
        TimeOffTypeId = type.Id,
        StaffPositionId = positionId,
        UserId = userId,
        AllowanceHours = hours,
        IsUnlimited = unlimited
    };

    private static async Task MakePrimaryAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user, StaffPosition position)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = position.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task GetTypesAsync_CreatesTheThreeDefaultsOnce()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TimeOffPolicyService service = GetService(options);

        Result<List<TimeOffType>> first = await service.GetTypesAsync(company.Id);
        Result<List<TimeOffType>> second = await service.GetTypesAsync(company.Id);

        Assert.IsTrue(first.IsSuccess);
        CollectionAssert.AreEqual(new[] { "Paid holiday", "Unpaid leave", "Sickness" }, second.Data!.Select(x => x.Name).ToArray());
        Assert.IsFalse(second.Data.Single(x => x.Name == "Sickness").DeductsFromAllowance);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(3, await ctx.TimeOffTypes.CountAsync());
    }

    [TestMethod]
    public async Task GetTypesAsync_HidesArchivedUnlessAsked()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TimeOffPolicyService service = GetService(options);
        TimeOffType unpaid = await TypeNamedAsync(service, company, "Unpaid leave");

        await service.DeactivateTypeAsync(SchedulingTestHelpers.AdminScope, unpaid.Id);

        Assert.AreEqual(2, (await service.GetTypesAsync(company.Id)).Data!.Count);
        Assert.AreEqual(3, (await service.GetTypesAsync(company.Id, includeInactive: true)).Data!.Count);
    }

    [TestMethod]
    public async Task SaveTypeAsync_RejectsADuplicateNameIgnoringCase()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TimeOffPolicyService service = GetService(options);
        await service.GetTypesAsync(company.Id);

        Result<TimeOffType> result = await service.SaveTypeAsync(SchedulingTestHelpers.AdminScope,
            new TimeOffType { CompanyId = company.Id, Name = "  paid HOLIDAY " });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "already");
    }

    [TestMethod]
    public async Task SaveTypeAsync_RefusesAManagerFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<TimeOffType> result = await GetService(options).SaveTypeAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation),
            new TimeOffType { CompanyId = company.Id, Name = "Jury service" });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveTypeAsync_RefusesStaffWithoutTheManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<TimeOffType> result = await GetService(options).SaveTypeAsync(SchedulingTestHelpers.StaffScopeFor(location),
            new TimeOffType { CompanyId = company.Id, Name = "Jury service" });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ResolveForUserAsync_UserBeatsPositionBeatsCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile advisor = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        StaffPosition managerPosition = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Manager");
        StaffPosition advisorPosition = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Customer Service Advisor");
        await MakePrimaryAsync(options, manager, managerPosition);
        await MakePrimaryAsync(options, advisor, advisorPosition);
        await MakePrimaryAsync(options, newStarter, advisorPosition);

        TimeOffPolicyService service = GetService(options);
        TimeOffType holiday = await TypeNamedAsync(service, company, "Paid holiday");
        TimeOffType unpaid = await TypeNamedAsync(service, company, "Unpaid leave");
        LocationScope admin = SchedulingTestHelpers.AdminScope;

        await service.SaveAllowanceAsync(admin, Row(company, holiday, hours: 224));
        await service.SaveAllowanceAsync(admin, Row(company, holiday, hours: 240, positionId: managerPosition.Id));
        await service.SaveAllowanceAsync(admin, Row(company, unpaid, unlimited: true, positionId: advisorPosition.Id));
        await service.SaveAllowanceAsync(admin, Row(company, holiday, hours: 100, userId: newStarter.Id));

        Dictionary<Guid, ResolvedAllowance> forManager = (await service.ResolveForUserAsync(manager.Id, company.Id)).Data!;
        Dictionary<Guid, ResolvedAllowance> forAdvisor = (await service.ResolveForUserAsync(advisor.Id, company.Id)).Data!;
        Dictionary<Guid, ResolvedAllowance> forNewStarter = (await service.ResolveForUserAsync(newStarter.Id, company.Id)).Data!;

        Assert.AreEqual(240m, forManager[holiday.Id].Hours);
        Assert.AreEqual(ContractTier.Position, forManager[holiday.Id].Source);
        Assert.IsFalse(forManager[unpaid.Id].IsConfigured, "Nothing is set for unpaid leave at the manager's tiers.");
        Assert.AreEqual(0m, forManager[unpaid.Id].Hours);

        Assert.AreEqual(224m, forAdvisor[holiday.Id].Hours);
        Assert.AreEqual(ContractTier.Company, forAdvisor[holiday.Id].Source);
        Assert.IsTrue(forAdvisor[unpaid.Id].IsUnlimited);

        Assert.AreEqual(100m, forNewStarter[holiday.Id].Hours);
        Assert.AreEqual(ContractTier.User, forNewStarter[holiday.Id].Source);
    }

    [TestMethod]
    public async Task SaveAllowanceAsync_UpdatesTheSameTierRowRatherThanAddingAnother()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TimeOffPolicyService service = GetService(options);
        TimeOffType holiday = await TypeNamedAsync(service, company, "Paid holiday");

        await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(company, holiday, hours: 200));
        await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(company, holiday, hours: 224));

        await using ApplicationDbContext ctx = new(options);
        TimeOffAllowance only = await ctx.TimeOffAllowances.SingleAsync();
        Assert.AreEqual(224m, only.AllowanceHours);
    }

    [TestMethod]
    public async Task RemoveAllowanceAsync_FallsBackToTheTierBelow()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TimeOffPolicyService service = GetService(options);
        TimeOffType holiday = await TypeNamedAsync(service, company, "Paid holiday");

        await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(company, holiday, hours: 224));
        await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(company, holiday, hours: 50, userId: user.Id));
        await service.RemoveAllowanceAsync(SchedulingTestHelpers.AdminScope, company.Id, holiday.Id, null, user.Id);

        ResolvedAllowance resolved = (await service.ResolveForUserAsync(user.Id, company.Id)).Data![holiday.Id];

        Assert.AreEqual(224m, resolved.Hours);
        Assert.AreEqual(ContractTier.Company, resolved.Source);
    }

    [TestMethod]
    public async Task SaveAllowanceAsync_RejectsNegativeHoursAndAnotherCompanysType()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        TimeOffPolicyService service = GetService(options);
        TimeOffType holiday = await TypeNamedAsync(service, company, "Paid holiday");

        Result<TimeOffAllowance> negative = await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(company, holiday, hours: -8));
        Result<TimeOffAllowance> wrongCompany = await service.SaveAllowanceAsync(SchedulingTestHelpers.AdminScope, Row(other, holiday, hours: 8));

        Assert.IsFalse(negative.IsSuccess);
        Assert.IsFalse(wrongCompany.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAllowanceAsync_RefusesAManagerEditingSomeoneAtAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile outsider = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Olly");
        TimeOffPolicyService service = GetService(options);
        TimeOffType holiday = await TypeNamedAsync(service, company, "Paid holiday");

        Result<TimeOffAllowance> result = await service.SaveAllowanceAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            Row(company, holiday, hours: 8, userId: outsider.Id));

        Assert.IsFalse(result.IsSuccess);
    }
}
