using Lanyard.Application.Services.BookingImport;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.OperationsAlerts;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.BookingImport;

// The Just Step import asks the party staffing watch about the days each run changed, once per run.
[TestClass]
public class BookingImportStaffingTests
{
    private sealed class PassThroughProtector : IDataProtector, IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    private static DateOnly Today => RotaTime.Today(DateTime.UtcNow);

    private static ExternalBookingDto Booking(string reference, int daysAhead, int startHour = 11, string type = "Adventure Play Party")
    {
        DateOnly date = Today.AddDays(daysAhead);
        return new(reference, date, new TimeOnly(startHour, 0), new TimeOnly(startHour + 2, 0),
            type, "Contact " + reference, "07000000000", "Child " + reference, date.AddYears(-7), 10, 180m, 50m, 130m, false);
    }

    private sealed record Setup(
        DbContextOptions<ApplicationDbContext> Options, Company Company, Location Location,
        BookingImportService Service, Mock<IJustStepClient> Client, Mock<IPartyStaffingWatch> Watch);

    private static async Task<Setup> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        ISystemDbContextFactory factory = SchedulingTestHelpers.GetFactory(options);

        Mock<IJustStepClient> client = new();
        Mock<IPartyStaffingWatch> watch = new();
        watch.Setup(w => w.CheckDaysAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<DateOnly>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PartyStaffingCheckOutcome>.Ok(PartyStaffingCheckOutcome.Empty));

        IOperationsAlertWriter writer = new OperationsAlertWriter(factory, new RecordingNotificationDispatcher(),
            new OperationsAlertEventBus(), TimeProvider.System, NullLogger<OperationsAlertWriter>.Instance);

        BookingImportService service = new(factory, factory, client.Object, writer, new PassThroughProtector(), TimeProvider.System,
            NullLogger<BookingImportService>.Instance, null, watch.Object);

        Result<BookingImportSettingsView> saved = await service.SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new BookingImportSettingsInput(location.Id, true, "venue@example.com", "secret-password", 1, 7, 23), "admin");
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        return new Setup(options, company, location, service, client, watch);
    }

    private static void Fetch(Setup s, params ExternalBookingDto[] bookings) =>
        s.Client.Setup(c => c.FetchBookingsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ExternalBookingDto>>.Ok(bookings));

    private static Task<BookingImportRunView> RunAsync(Setup s) =>
        s.Service.RunImportAsync(s.Company.Id, s.Location.Id, BookingImportTrigger.Scheduled);

    private static void VerifyChecked(Setup s, params DateOnly[] days) =>
        s.Watch.Verify(w => w.CheckDaysAsync(s.Location.Id,
            It.Is<IReadOnlyCollection<DateOnly>>(d => d.Count == days.Length && days.All(d.Contains)), null, It.IsAny<CancellationToken>()), Times.Once);

    [TestMethod]
    public async Task NewBookings_OneCheckForTheRun_WithEachDistinctDay()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3), Booking("2", 3, 15), Booking("3", 5));

        await RunAsync(s);

        VerifyChecked(s, Today.AddDays(3), Today.AddDays(5));
    }

    [TestMethod]
    public async Task UpdateThatMovesTheParty_ChecksTheOldAndNewDays()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3));
        await RunAsync(s);
        s.Watch.Invocations.Clear();

        Fetch(s, Booking("1", 6));
        await RunAsync(s);

        VerifyChecked(s, Today.AddDays(3), Today.AddDays(6));
    }

    [TestMethod]
    public async Task UpdateThatChangesOnlyTheGuestCount_NoCheck()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3));
        await RunAsync(s);
        s.Watch.Invocations.Clear();

        ExternalBookingDto bigger = Booking("1", 3) with { Quantity = 40 };
        Fetch(s, bigger);
        BookingImportRunView run = await RunAsync(s);

        Assert.AreEqual(1, run.Updated, "the booking is still updated");
        s.Watch.Verify(w => w.CheckDaysAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<DateOnly>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdateThatChangesTheType_ChecksTheDay()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3));
        await RunAsync(s);
        s.Watch.Invocations.Clear();

        Fetch(s, Booking("1", 3, type: "Laser Party"));
        await RunAsync(s);

        VerifyChecked(s, Today.AddDays(3));
    }

    [TestMethod]
    public async Task DroppedBooking_IsCancelledAndItsDayChecked()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3), Booking("2", 4));
        await RunAsync(s);
        s.Watch.Invocations.Clear();

        Fetch(s, Booking("1", 3));
        await RunAsync(s);

        VerifyChecked(s, Today.AddDays(4));
    }

    [TestMethod]
    public async Task UnchangedRun_NoCheck()
    {
        Setup s = await SeedAsync();
        Fetch(s, Booking("1", 3));
        await RunAsync(s);
        s.Watch.Invocations.Clear();

        BookingImportRunView run = await RunAsync(s);

        Assert.AreEqual(BookingImportRunStatus.NoChanges, run.Status);
        s.Watch.Verify(w => w.CheckDaysAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<DateOnly>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A first import bringing a whole season in: alerts only for built days inside the horizon, and
    // one push for the lot.
    [TestMethod]
    public async Task FirstImportOfASeason_OneSummaryNotice_OnlyBuiltDaysInTheHorizon()
    {
        RecordingNotificationDispatcher notifications = new();

        // The real watch needs the same database, so it's built on the options of a plain seed.
        Setup plain = await SeedAsync();
        DbContextOptions<ApplicationDbContext> options = plain.Options;
        ISystemDbContextFactory factory = SchedulingTestHelpers.GetFactory(options);
        OperationsAlertWriter writer = new(factory, notifications, new OperationsAlertEventBus(), TimeProvider.System, NullLogger<OperationsAlertWriter>.Instance);
        PartyStaffingWatch watch = new(factory, writer, notifications, TimeProvider.System, NullLogger<PartyStaffingWatch>.Instance);
        Setup setup = plain with
        {
            Service = new BookingImportService(factory, factory, plain.Client.Object, writer, new PassThroughProtector(), TimeProvider.System,
                NullLogger<BookingImportService>.Instance, null, watch)
        };

        UserProfile manager = await OperationsAlertTestSupport.SeedPersonAsync(options, plain.Location, "Mia", "Manager");
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, plain.Company, "Host");

        await using (ApplicationDbContext ctx = new(options))
        {
            // An 09:00-14:00 template every day, one party Host per party, and a built rota (one
            // shift) on every day from tomorrow for 30 days.
            foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            {
                ShiftTemplate template = new()
                {
                    Id = Guid.NewGuid(), CompanyId = plain.Company.Id, LocationId = plain.Location.Id, Name = "Day",
                    DayOfWeek = day, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(14, 0)
                };
                template.Slots.Add(new ShiftTemplateSlot { Id = Guid.NewGuid(), ShiftTemplateId = template.Id, StaffPositionId = host.Id, Headcount = 1 });
                ctx.ShiftTemplates.Add(template);
            }

            ctx.PartyStaffingRules.Add(new PartyStaffingRule
            {
                Id = Guid.NewGuid(), CompanyId = plain.Company.Id, LocationId = plain.Location.Id, StaffPositionId = host.Id, StaffCount = 1
            });

            for (int ahead = 1; ahead <= 30; ahead++)
            {
                ctx.Shifts.Add(new Shift
                {
                    Id = Guid.NewGuid(), LocationId = plain.Location.Id, StaffPositionId = host.Id,
                    StartUtc = RotaTime.ToUtc(Today.AddDays(ahead), new TimeOnly(9, 0)),
                    EndUtc = RotaTime.ToUtc(Today.AddDays(ahead), new TimeOnly(14, 0)),
                    CreateDate = DateTime.UtcNow, CreateByUserId = "seed", IsActive = true
                });
            }

            await ctx.SaveChangesAsync();
        }

        // Sixty days of parties, one a day from tomorrow.
        Fetch(setup, [.. Enumerable.Range(1, 60).Select(ahead => Booking($"r{ahead}", ahead))]);

        BookingImportRunView run = await RunAsync(setup);

        Assert.AreEqual(60, run.Imported);

        await using ApplicationDbContext check = new(options);
        int open = await check.OperationsAlerts.IgnoreQueryFilters()
            .CountAsync(x => x.Type == OperationsAlertType.PartyStaffingOutOfDate && x.Status != OperationsAlertStatus.Resolved);
        Assert.AreEqual(IPartyStaffingWatch.HorizonDays, open, "tomorrow to today+14, and nothing past the horizon");

        NotificationJob notice = notifications.Jobs.Single(x => x.Topic == NotificationTopic.PartyStaffingOutOfDate);
        Assert.AreEqual(manager.Id, notice.UserId);
        StringAssert.Contains(((OperationsAlertPayload)notice.Payload).Title, $"{IPartyStaffingWatch.HorizonDays} days");
    }
}
