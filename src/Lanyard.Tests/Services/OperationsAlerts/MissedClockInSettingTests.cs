using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.OperationsAlerts;

[TestClass]
public class MissedClockInSettingTests
{
    private static async Task<(SchedulingSettingsService Service, Company Company)> BuildAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        return (new SchedulingSettingsService(SchedulingTestHelpers.GetFactory(options)), company);
    }

    [TestMethod]
    public void NewCompanies_GetFifteenMinutes()
    {
        Assert.AreEqual(15, new CompanySchedulingSettings { CompanyId = 1 }.MissedClockInAlertMinutes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(240)]
    public async Task Save_KeepsAValueInRange(int minutes)
    {
        (SchedulingSettingsService service, Company company) = await BuildAsync();

        Result<CompanySchedulingSettings> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, MissedClockInAlertMinutes = minutes });

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(minutes, (await service.GetSettingsAsync(company.Id)).Data!.MissedClockInAlertMinutes);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(241)]
    public async Task Save_RefusesAValueOutOfRange(int minutes)
    {
        (SchedulingSettingsService service, Company company) = await BuildAsync();

        Result<CompanySchedulingSettings> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new CompanySchedulingSettings { CompanyId = company.Id, MissedClockInAlertMinutes = minutes });

        Assert.IsFalse(saved.IsSuccess);
        Assert.Contains("missed clock-in", saved.Error!);
    }
}
