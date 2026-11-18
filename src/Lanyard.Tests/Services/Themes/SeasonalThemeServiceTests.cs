using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Themes;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Themes;

[TestClass]
public class SeasonalThemeServiceTests
{
    // A November day: outside every season, so Automatic shows nothing.
    private static readonly DateTime November = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static SeasonalThemeService Service(DbContextOptions<ApplicationDbContext>? options = null, DateTime? now = null) =>
        new(SchedulingTestHelpers.GetFactory(options ?? SchedulingTestHelpers.GetInMemoryOptions()),
            new TestClock(now ?? November),
            NullLogger<SeasonalThemeService>.Instance);

    [TestMethod]
    [DataRow(2026, 11, 30, 23, 59, SeasonalTheme.None, DisplayName = "Last minute of November")]
    [DataRow(2026, 12, 1, 0, 0, SeasonalTheme.Christmas, DisplayName = "First minute of December")]
    [DataRow(2026, 12, 26, 23, 59, SeasonalTheme.Christmas, DisplayName = "Last minute of Boxing Day")]
    [DataRow(2026, 12, 27, 0, 0, SeasonalTheme.None, DisplayName = "27 December")]
    [DataRow(2026, 10, 23, 22, 59, SeasonalTheme.None, DisplayName = "23:59 BST on 23 October")]
    [DataRow(2026, 10, 23, 23, 30, SeasonalTheme.Halloween, DisplayName = "00:30 BST on 24 October (still 23 Oct in UTC)")]
    [DataRow(2026, 10, 31, 23, 59, SeasonalTheme.Halloween, DisplayName = "Halloween night (GMT after clocks go back)")]
    [DataRow(2026, 11, 1, 0, 0, SeasonalTheme.None, DisplayName = "1 November")]
    public void ResolveForDate_FollowsVenueLocalCalendar(int year, int month, int day, int hour, int minute, SeasonalTheme expected)
    {
        DateTime utc = new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        Assert.AreEqual(expected, Service().ResolveForDate(utc));
    }

    [TestMethod]
    public void Resolve_Automatic_UsesTheDate()
    {
        SeasonalThemeService service = Service();

        Assert.AreEqual(SeasonalTheme.None, service.Resolve(SeasonalThemeSetting.Automatic, true, November));
        Assert.AreEqual(SeasonalTheme.Christmas, service.Resolve(SeasonalThemeSetting.Automatic, true, new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void Resolve_CompanyOff_HidesTheSeason()
    {
        Assert.AreEqual(SeasonalTheme.None,
            Service().Resolve(SeasonalThemeSetting.Off, true, new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    [DataRow(SeasonalThemeSetting.Christmas, SeasonalTheme.Christmas)]
    [DataRow(SeasonalThemeSetting.Halloween, SeasonalTheme.Halloween)]
    public void Resolve_ForcedSeason_ShowsOutsideItsWindow(SeasonalThemeSetting setting, SeasonalTheme expected)
    {
        Assert.AreEqual(expected, Service().Resolve(setting, true, November));
    }

    [TestMethod]
    public void Resolve_UserOptOut_BeatsAForcedSeason()
    {
        Assert.AreEqual(SeasonalTheme.None, Service().Resolve(SeasonalThemeSetting.Christmas, false, November));
    }

    [TestMethod]
    public void Resolve_WithoutADate_UsesTheClock()
    {
        SeasonalThemeService service = Service(now: new DateTime(2026, 10, 30, 18, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(SeasonalTheme.Halloween, service.Resolve(SeasonalThemeSetting.Automatic, true));
    }

    [TestMethod]
    public async Task UserOptIn_DefaultsOn_AndRoundTrips()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = "amy", UserName = "amy", FirstName = "Amy" });
            await ctx.SaveChangesAsync();
        }

        SeasonalThemeService service = Service(options);

        Result<bool> initial = await service.GetUserOptInAsync("amy");
        Assert.IsTrue(initial.IsSuccess, initial.Error);
        Assert.IsTrue(initial.Data);

        Result<bool> saved = await service.SetUserOptInAsync("amy", false);
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Result<bool> reloaded = await service.GetUserOptInAsync("amy");
        Assert.IsFalse(reloaded.Data);
    }

    [TestMethod]
    public async Task UserOptIn_UnknownUser_Fails()
    {
        SeasonalThemeService service = Service();

        Assert.IsFalse((await service.GetUserOptInAsync("nobody")).IsSuccess);
        Assert.IsFalse((await service.SetUserOptInAsync("nobody", false)).IsSuccess);
    }

    [TestMethod]
    public async Task CompanySetting_IsSavedAndReturnedWithBranding()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        CompanyLocationService companies = new(SchedulingTestHelpers.GetFactory(options));

        Result<Company> created = await companies.SaveCompanyAsync(new Company { Name = "Play2Day" });
        Assert.IsTrue(created.IsSuccess, created.Error);

        Result<Company> updated = await companies.SaveCompanyAsync(new Company
        {
            Id = created.Data!.Id,
            Name = "Play2Day",
            SeasonalThemeSetting = SeasonalThemeSetting.Halloween,
        });
        Assert.IsTrue(updated.IsSuccess, updated.Error);

        Result<CompanyBrandingInfo> branding = await companies.GetCompanyBrandingAsync(created.Data.Id);
        Assert.AreEqual(SeasonalThemeSetting.Halloween, branding.Data!.SeasonalThemeSetting);
    }
}
