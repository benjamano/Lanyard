using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
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
    [DataRow(2, 29)]
    public async Task SaveSettingsAsync_RejectsInvalidFinancialYearStart(int month, int day)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = month, FinancialYearStartDay = day });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_AcceptsTwentyEighthOfFebruary()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, FinancialYearStartMonth = 2, FinancialYearStartDay = 28 });

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_RejectsZeroHoursPerDay()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, HoursPerDay = 0 });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "greater than 0");
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
    public async Task GetSettingsAsync_DefaultsToCalendarMonthsAndNoBreakDeduction()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).GetSettingsAsync(company.Id);

        Assert.AreEqual(PayPeriodFrequency.Monthly, result.Data!.PayPeriod);
        Assert.AreEqual(1, result.Data.PayPeriodStartDay);
        Assert.IsNull(result.Data.PayPeriodAnchorDate);
        Assert.AreEqual(BreakDeductionRule.None, result.Data.BreakDeduction);
        Assert.AreEqual(20, result.Data.BreakDeductionMinutes);
        Assert.AreEqual(6m, result.Data.BreakDeductionAfterHours);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_StoresThePayrollExportSettings()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        SchedulingSettingsService service = GetService(options);

        Result<CompanySchedulingSettings> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope, new CompanySchedulingSettings
        {
            CompanyId = company.Id,
            PayPeriod = PayPeriodFrequency.Fortnightly,
            PayPeriodAnchorDate = new DateOnly(2026, 10, 5),
            BreakDeduction = BreakDeductionRule.FixedOverThreshold,
            BreakDeductionMinutes = 30,
            BreakDeductionAfterHours = 7.5m
        });

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        CompanySchedulingSettings loaded = (await service.GetSettingsAsync(company.Id)).Data!;
        Assert.AreEqual(PayPeriodFrequency.Fortnightly, loaded.PayPeriod);
        Assert.AreEqual(new DateOnly(2026, 10, 5), loaded.PayPeriodAnchorDate);
        Assert.AreEqual(BreakDeductionRule.FixedOverThreshold, loaded.BreakDeduction);
        Assert.AreEqual(30, loaded.BreakDeductionMinutes);
        Assert.AreEqual(7.5m, loaded.BreakDeductionAfterHours);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_MonthlyClearsAnyOldAnchorDate()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        SchedulingSettingsService service = GetService(options);

        await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, PayPeriod = PayPeriodFrequency.Weekly, PayPeriodAnchorDate = new DateOnly(2026, 10, 5) });
        await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, PayPeriod = PayPeriodFrequency.Monthly, PayPeriodStartDay = 26, PayPeriodAnchorDate = new DateOnly(2026, 10, 5) });

        CompanySchedulingSettings loaded = (await service.GetSettingsAsync(company.Id)).Data!;
        Assert.AreEqual(26, loaded.PayPeriodStartDay);
        Assert.IsNull(loaded.PayPeriodAnchorDate);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(29)]
    public async Task SaveSettingsAsync_RejectsAMonthlyStartDayOutside1To28(int startDay)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, PayPeriod = PayPeriodFrequency.Monthly, PayPeriodStartDay = startDay });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "between 1 and 28");
    }

    [TestMethod]
    public async Task SaveSettingsAsync_RejectsAWeeklyPeriodWithNoAnchor()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, PayPeriod = PayPeriodFrequency.FourWeekly });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "first day of any one pay period");
    }

    [TestMethod]
    public async Task SaveSettingsAsync_RejectsAFixedBreakOfZeroMinutes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, BreakDeduction = BreakDeductionRule.FixedOverThreshold, BreakDeductionMinutes = 0 });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "between 1 and 120 minutes");
    }

    [TestMethod]
    public async Task SaveSettingsAsync_IgnoresFixedBreakNumbersWhenTheRuleIsOff()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CompanySchedulingSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, BreakDeduction = BreakDeductionRule.None, BreakDeductionMinutes = 0 });

        Assert.IsTrue(result.IsSuccess, result.Error);
    }
}
