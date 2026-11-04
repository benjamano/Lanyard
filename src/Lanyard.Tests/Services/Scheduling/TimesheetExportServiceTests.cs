using System.Text;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

// The export service over the real TimeEntryService, so the location check under test is the
// timesheet's own - the export must never be able to see more than the Timesheets page does.
[TestClass]
public class TimesheetExportServiceTests
{
    // Monday 5 October 2026 is British Summer Time.
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static DateTime Local(DateOnly day, int hour, int minute = 0) => RotaTime.ToUtc(day, new TimeOnly(hour, minute));

    private sealed class Fixture
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required TimesheetExportService Service { get; init; }
        public required CapturingLogger<TimesheetExportService> Logger { get; init; }
        public required Location Location { get; init; }
        public required Location OtherLocation { get; init; }
        public required UserProfile Amy { get; init; }
    }

    private static async Task<Fixture> CreateAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");

        Location other;
        await using (ApplicationDbContext ctx = new(options))
        {
            other = new Location { CompanyId = company.Id, Name = "Colchester", IsActive = true };
            ctx.Locations.Add(other);
            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(Local(Monday.AddDays(7), 9));
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);

        TimeEntryService timeEntries = new(
            factory,
            new ClockInPinService(factory, new PasswordHasher<UserProfile>()),
            new ClockInTerminalService(factory, clock, NullLogger<ClockInTerminalService>.Instance),
            new SchedulingSettingsService(factory),
            new TerminalEphemeralTokenService(clock),
            new TerminalEventBus(),
            clock,
            NullLogger<TimeEntryService>.Instance);

        CapturingLogger<TimesheetExportService> logger = new();

        return new Fixture
        {
            Options = options,
            Service = new TimesheetExportService(timeEntries, new SchedulingSettingsService(factory), logger),
            Logger = logger,
            Location = location,
            OtherLocation = other,
            Amy = amy
        };
    }

    private static async Task SeedEntryAsync(Fixture f, DateOnly day, int inHour, int outHour, bool approved = true, bool isActive = true, bool open = false, Location? location = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);

        ctx.TimeEntries.Add(new TimeEntry
        {
            Id = Guid.NewGuid(),
            UserId = f.Amy.Id,
            LocationId = (location ?? f.Location).Id,
            ClockInUtc = Local(day, inHour),
            ClockOutUtc = open ? null : Local(day, outHour),
            ApprovedDateUtc = approved ? Local(day, outHour) : null,
            ApprovedByUserId = approved ? "manager" : null,
            IsActive = isActive
        });

        await ctx.SaveChangesAsync();
    }

    private static string CsvText(TimesheetExportFile file) => Encoding.UTF8.GetString(file.Content[3..]);

    [TestMethod]
    public async Task ExportCsvAsync_Admin_GetsTheApprovedHours()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("timesheet-ipswich-2026-10-05-to-2026-10-11.csv", result.Data!.FileName);
        Assert.AreEqual("text/csv", result.Data.ContentType);
        StringAssert.Contains(CsvText(result.Data), "Day,Amy Tester,2026-10-05,09:00,17:00,1,480,8.00,,0,480,8.00,Approved");
        Assert.AreEqual(480, result.Data.Summary.ApprovedMinutes);
    }

    [TestMethod]
    public async Task ExportCsvAsync_ManagerOfTheLocation_Succeeds()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.ManagerScopeFor(f.Location), f.Location.Id, Monday, Monday.AddDays(6), "manager");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Summary.People);
    }

    [TestMethod]
    public async Task ExportCsvAsync_ManagerOfAnotherLocation_IsRefused()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.ManagerScopeFor(f.OtherLocation), f.Location.Id, Monday, Monday.AddDays(6), "manager");

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
        StringAssert.Contains(result.Error, "your own location");
    }

    [TestMethod]
    public async Task ExportCsvAsync_StaffScope_IsRefused()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.StaffScopeFor(f.Location), f.Location.Id, Monday, Monday.AddDays(6), f.Amy.Id);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task ExportCsvAsync_EndBeforeStart_Fails()
    {
        Fixture f = await CreateAsync();

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(-1), "admin");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "end of the range");
    }

    [TestMethod]
    public async Task ExportCsvAsync_RangeLongerThanTheLimit_Fails()
    {
        Fixture f = await CreateAsync();

        Result<TimesheetExportFile> ok = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(TimesheetExportService.MaxRangeDays - 1), "admin");
        Result<TimesheetExportFile> tooLong = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(TimesheetExportService.MaxRangeDays), "admin");

        Assert.IsTrue(ok.IsSuccess, ok.Error);
        Assert.IsFalse(tooLong.IsSuccess);
        StringAssert.Contains(tooLong.Error, "93 days");
    }

    [TestMethod]
    public async Task ExportCsvAsync_IgnoresRemovedEntriesAndOtherLocations()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17, isActive: false);
        await SeedEntryAsync(f, Monday.AddDays(1), 9, 17, location: f.OtherLocation);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Row type,Employee,Date,First in,Last out,Entries,Minutes,Hours,Rota break (min),Break deducted (min),Paid minutes,Paid hours,Status\r\n", CsvText(result.Data!));
        Assert.AreEqual(0, result.Data!.Summary.People);
    }

    [TestMethod]
    public async Task PreviewAsync_MatchesTheExport_AndCountsWhatIsLeftOut()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);
        await SeedEntryAsync(f, Monday.AddDays(1), 9, 17, approved: false);
        await SeedEntryAsync(f, Monday.AddDays(2), 9, 17, approved: false, open: true);

        Result<TimesheetExportSummary> preview = await f.Service.PreviewAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6));
        Result<TimesheetExportFile> export = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");

        Assert.IsTrue(preview.IsSuccess, preview.Error);
        Assert.AreEqual(export.Data!.Summary, preview.Data);
        Assert.AreEqual(480, preview.Data!.ApprovedMinutes);
        Assert.AreEqual(1, preview.Data.AwaitingApproval);
        Assert.AreEqual(1, preview.Data.StillClockedIn);
        Assert.AreEqual(2, preview.Data.ExcludedCount);
    }

    [TestMethod]
    public async Task PreviewAsync_ManagerOfAnotherLocation_IsRefused()
    {
        Fixture f = await CreateAsync();

        Result<TimesheetExportSummary> result = await f.Service.PreviewAsync(SchedulingTestHelpers.ManagerScopeFor(f.OtherLocation), f.Location.Id, Monday, Monday.AddDays(6));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ExportCsvAsync_NeverLogsStaffNames()
    {
        Fixture f = await CreateAsync();
        await SeedEntryAsync(f, Monday, 9, 17);
        await SeedEntryAsync(f, Monday.AddDays(1), 9, 17, approved: false);

        await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");
        await f.Service.ExportCsvAsync(SchedulingTestHelpers.ManagerScopeFor(f.OtherLocation), f.Location.Id, Monday, Monday.AddDays(6), "manager");

        Assert.HasCount(2, f.Logger.Messages);
        Assert.IsTrue(f.Logger.Messages.All(m => !m.Contains("Amy") && !m.Contains("Tester")), string.Join("\n", f.Logger.Messages));
    }

    private static async Task SeedSettingsAsync(DbContextOptions<ApplicationDbContext> options, int companyId, Action<CompanySchedulingSettings> configure)
    {
        await using ApplicationDbContext ctx = new(options);

        CompanySchedulingSettings settings = new() { Id = Guid.NewGuid(), CompanyId = companyId };
        configure(settings);
        ctx.CompanySchedulingSettings.Add(settings);
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ExportCsvAsync_AppliesTheCompanysBreakRule()
    {
        Fixture f = await CreateAsync();
        await SeedSettingsAsync(f.Options, f.Location.CompanyId, s =>
        {
            s.BreakDeduction = BreakDeductionRule.FixedOverThreshold;
            s.BreakDeductionMinutes = 20;
            s.BreakDeductionAfterHours = 6;
        });
        await SeedEntryAsync(f, Monday, 9, 17);
        await SeedEntryAsync(f, Monday.AddDays(1), 9, 13);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");

        Assert.IsTrue(result.IsSuccess, result.Error);
        string csv = CsvText(result.Data!);
        StringAssert.Contains(csv, "Day,Amy Tester,2026-10-05,09:00,17:00,1,480,8.00,,20,460,7.67,Approved");
        StringAssert.Contains(csv, "Day,Amy Tester,2026-10-06,09:00,13:00,1,240,4.00,,0,240,4.00,Approved");
        Assert.AreEqual(700, result.Data!.Summary.PaidMinutes);
        Assert.AreEqual(20, result.Data.Summary.BreakDeductedMinutes);
    }

    [TestMethod]
    public async Task ExportCsvAsync_NeverUsesAnotherCompanysBreakRule()
    {
        Fixture f = await CreateAsync();
        (Company otherCompany, _) = await SchedulingTestHelpers.SeedCompanyAsync(f.Options, "Other Co", "Elsewhere");
        await SeedSettingsAsync(f.Options, otherCompany.Id, s => s.BreakDeduction = BreakDeductionRule.FixedOverThreshold);
        await SeedEntryAsync(f, Monday, 9, 17);

        Result<TimesheetExportFile> result = await f.Service.ExportCsvAsync(SchedulingTestHelpers.AdminScope, f.Location.Id, Monday, Monday.AddDays(6), "admin");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Summary.BreakDeductedMinutes);
        Assert.AreEqual(480, result.Data.Summary.PaidMinutes);
    }

    [TestMethod]
    public async Task GetPayPeriodPresetsAsync_ReturnsTheCurrentAndLastPeriods()
    {
        Fixture f = await CreateAsync();
        await SeedSettingsAsync(f.Options, f.Location.CompanyId, s =>
        {
            s.PayPeriod = PayPeriodFrequency.Monthly;
            s.PayPeriodStartDay = 26;
            s.BreakDeduction = BreakDeductionRule.RotaBreak;
        });

        Result<PayPeriodPresets> result = await f.Service.GetPayPeriodPresetsAsync(f.Location.CompanyId, new DateOnly(2026, 10, 13));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new PayPeriod(new DateOnly(2026, 9, 26), new DateOnly(2026, 10, 25)), result.Data!.Current);
        Assert.AreEqual(new PayPeriod(new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25)), result.Data.Previous);
        Assert.AreEqual("Monthly, 26th to 25th", result.Data.Description);
        Assert.IsTrue(result.Data.IsConfigured);
        Assert.AreEqual(BreakDeductionRule.RotaBreak, result.Data.Breaks.Rule);
    }

    [TestMethod]
    public async Task GetPayPeriodPresetsAsync_WithNoSettings_IsTheCalendarMonth()
    {
        Fixture f = await CreateAsync();

        Result<PayPeriodPresets> result = await f.Service.GetPayPeriodPresetsAsync(f.Location.CompanyId, new DateOnly(2026, 10, 13));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new PayPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), result.Data!.Previous);
        Assert.AreEqual(BreakDeductionPolicy.None, result.Data.Breaks);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
