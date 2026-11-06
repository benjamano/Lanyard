using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartyStaffingServiceTests
{
    private const string ManagerId = "manager-user";

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Ipswich, StaffPosition Host)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Party Host");

        return new World(options, company, ipswich, host);
    }

    private static PartyStaffingService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<PartyStaffingService>.Instance);

    private static PartyStaffingRule Rule(World w, string? type, int count = 2) => new()
    {
        CompanyId = 0,
        LocationId = w.Ipswich.Id,
        PartyType = type,
        StaffPositionId = w.Host.Id,
        StaffCount = count
    };

    [TestMethod]
    public async Task SaveRuleAsync_New_SavedWithTrimmedTypeUnderTheCompany()
    {
        World w = await SeedAsync();

        Result<PartyStaffingRule> saved = await GetService(w.Options).SaveRuleAsync(w.ManagerScope, Rule(w, "  Lazer + Play "), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual("Lazer + Play", saved.Data!.PartyType);
        Assert.AreEqual(w.Company.Id, saved.Data.CompanyId);
    }

    [TestMethod]
    public async Task SaveRuleAsync_BlankType_IsTheDefaultRule()
    {
        World w = await SeedAsync();

        Result<PartyStaffingRule> saved = await GetService(w.Options).SaveRuleAsync(w.ManagerScope, Rule(w, "   "), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.IsNull(saved.Data!.PartyType);
    }

    [TestMethod]
    public async Task SaveRuleAsync_SameTypeAndPositionTwice_Refused()
    {
        World w = await SeedAsync();
        PartyStaffingService service = GetService(w.Options);

        await service.SaveRuleAsync(w.ManagerScope, Rule(w, "Play"), ManagerId);
        Result<PartyStaffingRule> again = await service.SaveRuleAsync(w.ManagerScope, Rule(w, "PLAY"), ManagerId);

        Assert.IsFalse(again.IsSuccess);
    }

    [TestMethod]
    public async Task SaveRuleAsync_OutOfRange_Refused()
    {
        World w = await SeedAsync();

        Assert.IsFalse((await GetService(w.Options).SaveRuleAsync(w.ManagerScope, Rule(w, "Play", count: 0), ManagerId)).IsSuccess);
        Assert.IsFalse((await GetService(w.Options).SaveRuleAsync(w.ManagerScope, Rule(w, "Play", count: 21), ManagerId)).IsSuccess);
    }

    [TestMethod]
    public async Task SaveRuleAsync_StaffScope_Refused()
    {
        World w = await SeedAsync();

        Assert.IsFalse((await GetService(w.Options).SaveRuleAsync(SchedulingTestHelpers.StaffScopeFor(w.Ipswich), Rule(w, "Play"), "staff")).IsSuccess);
    }

    [TestMethod]
    public async Task RemoveRuleAsync_NoLongerListed()
    {
        World w = await SeedAsync();
        PartyStaffingService service = GetService(w.Options);

        PartyStaffingRule saved = (await service.SaveRuleAsync(w.ManagerScope, Rule(w, "Play"), ManagerId)).Data!;

        Assert.IsTrue((await service.RemoveRuleAsync(w.ManagerScope, saved.Id, ManagerId)).IsSuccess);
        Assert.AreEqual(0, (await service.GetRulesAsync(w.ManagerScope, w.Ipswich.Id)).Data!.Count);
    }

    [TestMethod]
    public void For_OwnRulesWinOverDefault()
    {
        Guid host = Guid.NewGuid();
        List<PartyStaffingRule> rules =
        [
            new() { CompanyId = 1, LocationId = 1, PartyType = "Lazer", StaffPositionId = host, StaffCount = 2 },
            new() { CompanyId = 1, LocationId = 1, PartyType = null, StaffPositionId = host, StaffCount = 1 }
        ];

        Assert.AreEqual(2, PartyStaffingRule.For(rules, "lazer").Single().StaffCount);
        Assert.AreEqual(1, PartyStaffingRule.For(rules, "Play").Single().StaffCount);
        Assert.AreEqual(1, PartyStaffingRule.For(rules, "").Single().StaffCount);
    }
}
