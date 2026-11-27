using Lanyard.Application.Services.BookingImport;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Lanyard.Tests.TestUtilities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.BookingImport;

[TestClass]
public class BookingImportServiceTests
{
    private const string ActingUser = "acting-user";

    // Round-trips through base64 without real encryption, enough to prove Protect/Unprotect are used.
    private sealed class PassThroughProtector : IDataProtector, IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    private static (BookingImportService Service, Mock<IJustStepClient> Client, Mock<IOperationsAlertWriter> Alerts) GetService(
        DbContextOptions<ApplicationDbContext> options)
    {
        ISystemDbContextFactory factory = SchedulingTestHelpers.GetFactory(options);
        Mock<IJustStepClient> client = new();
        Mock<IOperationsAlertWriter> alerts = new();

        alerts.Setup(a => a.RaiseAsync(It.IsAny<OperationsAlertRaise>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Created));
        alerts.Setup(a => a.ResolveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        BookingImportService service = new(factory, factory, client.Object, alerts.Object,
            new PassThroughProtector(), TimeProvider.System, NullLogger<BookingImportService>.Instance);

        return (service, client, alerts);
    }

    // 30 days out so the party falls inside the import window (today-7 .. today+365), which the
    // drop/cancel reconciliation is scoped to.
    private static ExternalBookingDto Booking(string reference, bool provisional = false, int qty = 10)
    {
        DateOnly date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        return new(reference, date, new TimeOnly(11, 30), new TimeOnly(13, 30),
            "Adventure Play Party", "Contact " + reference, "07000000000", "Child " + reference,
            date.AddYears(-7), qty, 180m, 50m, 130m, provisional);
    }

    private void SetupFetch(Mock<IJustStepClient> client, params ExternalBookingDto[] bookings) =>
        client.Setup(c => c.FetchBookingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ExternalBookingDto>>.Ok(bookings));

    private static async Task EnableImportAsync(BookingImportService service, Location location)
    {
        Result<BookingImportSettingsView> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new BookingImportSettingsInput(location.Id, true, "venue@example.com", "secret-password", 1, 7, 23), ActingUser);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
    }

    [TestMethod]
    public async Task SaveSettings_StoresCredentials_ButNeverReturnsThePassword()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, _, _) = GetService(options);

        await EnableImportAsync(service, location);

        Result<BookingImportSettingsView> view = await service.GetSettingsAsync(SchedulingTestHelpers.AdminScope, location.Id);
        Assert.IsTrue(view.IsSuccess, view.Error);
        Assert.IsTrue(view.Data!.Enabled);
        Assert.AreEqual("venue@example.com", view.Data.Username);
        Assert.IsTrue(view.Data.HasPassword);

        // The stored password is encrypted, not the plain text, and the view has no password field at all.
        await using ApplicationDbContext ctx = new(options);
        BookingImportSettings row = await ctx.BookingImportSettings.SingleAsync();
        Assert.AreNotEqual("secret-password", row.PasswordProtected);
        Assert.IsTrue(row.PasswordProtected.Length > 0);
    }

    [TestMethod]
    public async Task SaveSettings_PersistsScheduleAndRejectsABadWindow()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, _, _) = GetService(options);

        Result<BookingImportSettingsView> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new BookingImportSettingsInput(location.Id, false, "venue@example.com", "pw", 2, 8, 22), ActingUser);
        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(2, saved.Data!.FrequencyHours);
        Assert.AreEqual(8, saved.Data.ActiveStartHour);
        Assert.AreEqual(22, saved.Data.ActiveEndHour);

        // End before start is not a valid daytime window.
        Result<BookingImportSettingsView> bad = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new BookingImportSettingsInput(location.Id, false, "venue@example.com", null, 1, 20, 6), ActingUser);
        Assert.IsFalse(bad.IsSuccess);
    }

    [TestMethod]
    public async Task SaveSettings_CannotEnableWithoutCredentials()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, _, _) = GetService(options);

        Result<BookingImportSettingsView> result = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new BookingImportSettingsInput(location.Id, true, "", null, 1, 7, 23), ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task RunImport_CreatesPartyBookings_AndResolvesAnyAlert()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, Mock<IOperationsAlertWriter> alerts) = GetService(options);

        await EnableImportAsync(service, location);
        SetupFetch(client, Booking("8388"), Booking("9107", provisional: true));

        BookingImportRunView run = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(BookingImportRunStatus.Success, run.Status);
        Assert.AreEqual(2, run.Imported);
        Assert.AreEqual(0, run.Updated);

        await using ApplicationDbContext ctx = new(options);
        List<PartyBooking> parties = await ctx.PartyBookings.ToListAsync();
        Assert.AreEqual(2, parties.Count);
        Assert.IsTrue(parties.All(p => p.ExternalSource == BookingImportService.Source && p.LocationId == location.Id && p.CompanyId == company.Id));
        Assert.IsTrue(parties.Single(p => p.ExternalRef == "9107").ExternalIsProvisional);
        Assert.AreEqual(1, await ctx.BookingImportRuns.CountAsync());

        alerts.Verify(a => a.ResolveAsync(company.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunImport_IsIdempotent_UpdatingRatherThanDuplicating()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        await EnableImportAsync(service, location);

        SetupFetch(client, Booking("8388", qty: 10));
        await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        // Same reference, changed details.
        SetupFetch(client, Booking("8388", qty: 25));
        BookingImportRunView second = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(0, second.Imported);
        Assert.AreEqual(1, second.Updated);

        await using ApplicationDbContext ctx = new(options);
        PartyBooking party = await ctx.PartyBookings.SingleAsync();
        Assert.AreEqual(25, party.ExpectedChildren);
    }

    [TestMethod]
    public async Task RunImport_SecondIdenticalRun_ChangesNothing()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        await EnableImportAsync(service, location);
        SetupFetch(client, Booking("8388"), Booking("9107", provisional: true));

        await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);
        BookingImportRunView second = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        // Nothing changed in Just Step, so the second run is a true no-op.
        Assert.AreEqual(BookingImportRunStatus.NoChanges, second.Status);
        Assert.AreEqual(0, second.Imported);
        Assert.AreEqual(0, second.Updated);

        await using ApplicationDbContext ctx = new(options);
        // The rows were never rewritten, so their audit UpdateDate is still unset.
        Assert.IsTrue(await ctx.PartyBookings.AllAsync(p => p.UpdateDate == null));
    }

    [TestMethod]
    public async Task RunImport_CancelsBookingsThatDroppedOutOfJustStep()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        await EnableImportAsync(service, location);
        SetupFetch(client, Booking("8388"), Booking("9107"));
        await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        // 9107 is gone from Just Step (cancelled there).
        SetupFetch(client, Booking("8388"));
        BookingImportRunView second = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(1, second.Updated);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(PartyBookingStatus.Cancelled, (await ctx.PartyBookings.SingleAsync(p => p.ExternalRef == "9107")).Status);
        Assert.AreEqual(PartyBookingStatus.Booked, (await ctx.PartyBookings.SingleAsync(p => p.ExternalRef == "8388")).Status);
    }

    [TestMethod]
    public async Task RunImport_EmptyReport_DoesNotCancelAnything()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        await EnableImportAsync(service, location);
        SetupFetch(client, Booking("8388"), Booking("9107"));
        await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        // A one-off empty response must not wipe the window.
        SetupFetch(client);
        await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(2, await ctx.PartyBookings.CountAsync(p => p.Status == PartyBookingStatus.Booked));
    }

    [TestMethod]
    public async Task RunImport_CollapsesDuplicateReferencesInOneFetch()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        await EnableImportAsync(service, location);
        // Same reference twice in one report - the real site does this.
        SetupFetch(client, Booking("8388", qty: 10), Booking("8388", qty: 20));

        BookingImportRunView run = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(BookingImportRunStatus.Success, run.Status);
        Assert.AreEqual(2, run.BookingsSeen);
        Assert.AreEqual(1, run.Imported);

        await using ApplicationDbContext ctx = new(options);
        PartyBooking party = await ctx.PartyBookings.SingleAsync();
        Assert.AreEqual(20, party.ExpectedChildren);
    }

    [TestMethod]
    public async Task RunImport_BadLogin_RecordsLoginFailedAndRaisesAlert()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, Mock<IOperationsAlertWriter> alerts) = GetService(options);

        await EnableImportAsync(service, location);
        client.Setup(c => c.FetchBookingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ExternalBookingDto>>.Fail("Just Step Online rejected those sign-in details."));

        BookingImportRunView run = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(BookingImportRunStatus.LoginFailed, run.Status);
        alerts.Verify(a => a.RaiseAsync(
            It.Is<OperationsAlertRaise>(r => r.Type == OperationsAlertType.PartyBookingImportFailed
                && r.CompanyId == company.Id && r.LocationId == location.Id
                && r.NotifyTopic == NotificationTopic.PartyBookingImportFailed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunImport_SkippedWhenDisabled_AndImportsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, _) = GetService(options);

        // No settings saved at all.
        BookingImportRunView run = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Scheduled);

        Assert.AreEqual(BookingImportRunStatus.Skipped, run.Status);
        client.Verify(c => c.FetchBookingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.PartyBookings.CountAsync());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public async Task RunImport_PartiesSwitchedOff_SkipsWithoutFetching_AndClearsTheAlert()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (BookingImportService service, Mock<IJustStepClient> client, Mock<IOperationsAlertWriter> alerts) = GetService(options);

        await EnableImportAsync(service, location);
        SetupFetch(client, Booking("8388"));

        await using (ApplicationDbContext ctx = new(options))
        {
            await TestFeatures.SwitchOffForCompanyAsync(ctx, company.Id, CompanyFeature.Parties);
            await ctx.SaveChangesAsync();
        }

        BookingImportRunView run = await service.RunImportAsync(company.Id, location.Id, BookingImportTrigger.Manual);

        Assert.AreEqual(BookingImportRunStatus.Skipped, run.Status);
        StringAssert.Contains(run.Error, "Parties");
        client.Verify(c => c.FetchBookingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        alerts.Verify(a => a.ResolveAsync(company.Id, $"juststep-import:{location.Id}", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(0, await check.PartyBookings.IgnoreQueryFilters().CountAsync());
    }

    [TestMethod]
    public async Task Sweep_SkipsLocationsWithoutParties()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company play2Day, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        (BookingImportService service, _, _) = GetService(options);

        await EnableImportAsync(service, ipswich);
        await EnableImportAsync(service, elsewhere);

        await using (ApplicationDbContext ctx = new(options))
        {
            await TestFeatures.SwitchOffForCompanyAsync(ctx, other.Id, CompanyFeature.Parties);
            await ctx.SaveChangesAsync();
        }

        Mock<IBookingImportService> import = new();
        ServiceCollection services = new();
        services.AddSingleton(import.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Midday in winter, inside the default 07:00-23:00 hours, with no run yet so both are due.
        JustStepImportHostedService sweep = new(SchedulingTestHelpers.GetFactory(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<JustStepImportHostedService>.Instance);

        int ran = await sweep.RunSweepAsync();

        Assert.AreEqual(1, ran);
        import.Verify(i => i.RunImportAsync(play2Day.Id, ipswich.Id, BookingImportTrigger.Scheduled, It.IsAny<CancellationToken>()), Times.Once);
        import.Verify(i => i.RunImportAsync(other.Id, elsewhere.Id, It.IsAny<BookingImportTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
