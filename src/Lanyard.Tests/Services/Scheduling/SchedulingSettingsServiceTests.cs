using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class SchedulingSettingsServiceTests
{
    private static SchedulingSettingsService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    [TestMethod]
    public async Task GetSettingsAsync_ReturnsDefaultsWhenNoRow()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).GetSettingsAsync(company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(4, result.Data!.FinancialYearStartMonth);
        Assert.AreEqual(6, result.Data.FinancialYearStartDay);
        Assert.AreEqual(8m, result.Data.HoursPerDay);
        Assert.AreEqual(24, result.Data.ShiftReminderLeadHours);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.CompanySchedulingSettings.CountAsync());
    }

    [TestMethod]
    public async Task SaveSettingsAsync_CreatesRowThenUpdatesIt()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        SchedulingSettingsService service = GetService(options);

        Result<CompanySchedulingSettings> first = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = 1, FinancialYearStartDay = 1, HoursPerDay = 7.5m, ShiftReminderLeadHours = 48 });
        Result<CompanySchedulingSettings> second = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = 4, FinancialYearStartDay = 6, HoursPerDay = 8, ShiftReminderLeadHours = 12 });

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        CompanySchedulingSettings saved = await ctx.CompanySchedulingSettings.SingleAsync();
        Assert.AreEqual(4, saved.FinancialYearStartMonth);
        Assert.AreEqual(12, saved.ShiftReminderLeadHours);
    }

    [TestMethod]
    [DataRow(13, 1)]
    [DataRow(0, 1)]
    [DataRow(4, 31)]
    [DataRow(2, 30)]
    public async Task SaveSettingsAsync_RejectsInvalidFinancialYearStart(int month, int day)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = month, FinancialYearStartDay = day });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_AcceptsTwentyNinthOfFebruary()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = 2, FinancialYearStartDay = 29 });

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(
            SchedulingTestHelpers.ManagerScopeFor(otherLocation), new CompanySchedulingSettings { CompanyId = company.Id });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ResolveCompanyIdForUserAsync_ReturnsTheUsersCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<int> result = await GetService(options).ResolveCompanyIdForUserAsync(user.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(company.Id, result.Data);
    }

    [TestMethod]
    public async Task ResolveCompanyIdForUserAsync_FailsWhenUserHasNoLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = "lonely", UserName = "lonely" });
            await ctx.SaveChangesAsync();
        }

        Result<int> result = await GetService(options).ResolveCompanyIdForUserAsync("lonely");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ResolveCompanyIdForUserAsync_FailsWhenUserSpansCompanies()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = otherLocation.Id, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Result<int> result = await GetService(options).ResolveCompanyIdForUserAsync(user.Id);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "more than one company");
    }
}
