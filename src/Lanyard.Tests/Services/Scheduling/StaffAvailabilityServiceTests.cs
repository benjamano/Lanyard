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
public class StaffAvailabilityServiceTests
{
    private const string ManagerId = "manager-user";

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Ipswich, UserProfile Amy, UserProfile Tom)
    {
        public LocationScope AmyScope => SchedulingTestHelpers.StaffScopeFor(Ipswich);
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Tom");

        return new World(options, company, ipswich, amy, tom);
    }

    private static StaffAvailabilityService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<StaffAvailabilityService>.Instance);

    private static StaffAvailability Window(UserProfile user, DayOfWeek day, int startHour, int endHour, AvailabilityKind kind = AvailabilityKind.Unavailable) => new()
    {
        CompanyId = 0,
        UserId = user.Id,
        DayOfWeek = day,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(endHour, 0),
        Kind = kind
    };

    // --- Their own ---

    [TestMethod]
    public async Task SaveAsync_OwnWindow_SavedUnderTheCompanyTheySignedInTo()
    {
        World w = await SeedAsync();

        Result<StaffAvailability> saved = await GetService(w.Options).SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 17, 23), w.Amy.Id);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(w.Company.Id, saved.Data!.CompanyId);
        Assert.IsNull(saved.Data.UpdatedByUserId);

        Result<List<StaffAvailability>> mine = await GetService(w.Options).GetAsync(w.AmyScope, w.Amy.Id, w.Amy.Id);
        Assert.AreEqual(1, mine.Data!.Count);
    }

    [TestMethod]
    public async Task SaveAsync_OwnWindow_SignedInAtAnotherCompany_Fails()
    {
        World w = await SeedAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");

        Result<StaffAvailability> saved = await GetService(w.Options)
            .SaveAsync(SchedulingTestHelpers.StaffScopeFor(elsewhere), Window(w.Amy, DayOfWeek.Tuesday, 17, 23), w.Amy.Id);

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAsync_StaffMemberEditingSomeoneElse_Fails()
    {
        World w = await SeedAsync();

        Result<StaffAvailability> saved = await GetService(w.Options).SaveAsync(w.AmyScope, Window(w.Tom, DayOfWeek.Tuesday, 17, 23), w.Amy.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "people you manage");
    }

    [TestMethod]
    public async Task GetAsync_StaffMemberReadingSomeoneElse_Fails()
    {
        World w = await SeedAsync();

        Result<List<StaffAvailability>> result = await GetService(w.Options).GetAsync(w.AmyScope, w.Tom.Id, w.Amy.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetAsync_OrdersMondayFirstThenByTime()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);

        await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Sunday, 9, 12), w.Amy.Id);
        await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Monday, 18, 22), w.Amy.Id);
        await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Monday, 6, 9), w.Amy.Id);

        List<StaffAvailability> rows = (await service.GetAsync(w.AmyScope, w.Amy.Id, w.Amy.Id)).Data!;

        CollectionAssert.AreEqual(new List<string> { "Mondays, 06:00–09:00", "Mondays, 18:00–22:00", "Sundays, 09:00–12:00" },
            rows.Select(StaffAvailabilityRules.Describe).ToList());
    }

    // --- Managers ---

    [TestMethod]
    public async Task SaveAsync_ManagerForTheirStaff_RecordsWhoSetIt()
    {
        World w = await SeedAsync();

        Result<StaffAvailability> saved = await GetService(w.Options).SaveAsync(w.ManagerScope, Window(w.Tom, DayOfWeek.Saturday, 0, 0), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(ManagerId, saved.Data!.UpdatedByUserId);
    }

    [TestMethod]
    public async Task SaveAsync_ManagerAtAnotherCompany_Fails()
    {
        World w = await SeedAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");

        Result<StaffAvailability> saved = await GetService(w.Options)
            .SaveAsync(SchedulingTestHelpers.ManagerScopeFor(elsewhere), Window(w.Tom, DayOfWeek.Saturday, 0, 0), ManagerId);

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAsync_AdminWithoutACompany_UsesThePersonsCompany()
    {
        World w = await SeedAsync();

        Result<StaffAvailability> saved = await GetService(w.Options).SaveAsync(SchedulingTestHelpers.AdminScope, Window(w.Tom, DayOfWeek.Saturday, 0, 0), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(w.Company.Id, saved.Data!.CompanyId);
    }

    // --- Validation, edits and removal ---

    [TestMethod]
    public async Task SaveAsync_OverlappingWindow_Refused()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);
        await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 9, 17), w.Amy.Id);

        Result<StaffAvailability> second = await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 16, 20, AvailabilityKind.Preferred), w.Amy.Id);

        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "overlaps");
    }

    [TestMethod]
    public async Task SaveAsync_EditingAWindow_DoesNotOverlapItself()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);
        StaffAvailability saved = (await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 9, 17), w.Amy.Id)).Data!;

        StaffAvailability edit = Window(w.Amy, DayOfWeek.Tuesday, 10, 18);
        edit.Id = saved.Id;
        edit.Notes = "  College  ";

        Result<StaffAvailability> result = await service.SaveAsync(w.AmyScope, edit, w.Amy.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new TimeOnly(10, 0), result.Data!.StartTime);
        Assert.AreEqual("College", result.Data.Notes);
        Assert.AreEqual(1, (await service.GetAsync(w.AmyScope, w.Amy.Id, w.Amy.Id)).Data!.Count);
    }

    [TestMethod]
    public async Task SaveAsync_EditingSomeoneElsesWindowById_Fails()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);
        StaffAvailability toms = (await service.SaveAsync(w.ManagerScope, Window(w.Tom, DayOfWeek.Tuesday, 9, 17), ManagerId)).Data!;

        StaffAvailability sneaky = Window(w.Amy, DayOfWeek.Tuesday, 9, 17);
        sneaky.Id = toms.Id;

        Result<StaffAvailability> result = await service.SaveAsync(w.AmyScope, sneaky, w.Amy.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAsync_EndDateBeforeStart_Refused()
    {
        World w = await SeedAsync();
        StaffAvailability row = Window(w.Amy, DayOfWeek.Tuesday, 9, 17);
        row.EffectiveFrom = new DateOnly(2026, 11, 1);
        row.EffectiveTo = new DateOnly(2026, 10, 1);

        Result<StaffAvailability> result = await GetService(w.Options).SaveAsync(w.AmyScope, row, w.Amy.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task RemoveAsync_SoftDeletes()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);
        StaffAvailability saved = (await service.SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 9, 17), w.Amy.Id)).Data!;

        Result<bool> removed = await service.RemoveAsync(w.AmyScope, saved.Id, w.Amy.Id);

        Assert.IsTrue(removed.IsSuccess, removed.Error);
        Assert.AreEqual(0, (await service.GetAsync(w.AmyScope, w.Amy.Id, w.Amy.Id)).Data!.Count);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.IsFalse((await ctx.StaffAvailabilities.SingleAsync()).IsActive);
    }

    [TestMethod]
    public async Task RemoveAsync_SomeoneElsesWindow_Fails()
    {
        World w = await SeedAsync();
        StaffAvailabilityService service = GetService(w.Options);
        StaffAvailability toms = (await service.SaveAsync(w.ManagerScope, Window(w.Tom, DayOfWeek.Tuesday, 9, 17), ManagerId)).Data!;

        Result<bool> removed = await service.RemoveAsync(w.AmyScope, toms.Id, w.Amy.Id);

        Assert.IsFalse(removed.IsSuccess);
    }

    // --- The rota warns, never blocks ---

    [TestMethod]
    public async Task RotaSaveShift_OnAnUnavailableTime_SavesWithAWarning()
    {
        World w = await SeedAsync();
        await GetService(w.Options).SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 17, 23), w.Amy.Id);

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), new RecordingNotificationDispatcher(), NullLogger<RotaService>.Instance);

        DateOnly tuesday = new(2026, 10, 6);
        Shift shift = new()
        {
            LocationId = w.Ipswich.Id,
            UserId = w.Amy.Id,
            StartUtc = RotaTime.ToUtc(tuesday, new TimeOnly(16, 0)),
            EndUtc = RotaTime.ToUtc(tuesday, new TimeOnly(20, 0)),
            CreateByUserId = ManagerId
        };

        Result<ShiftSaveResult> saved = await rota.SaveShiftAsync(w.ManagerScope, shift, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        CollectionAssert.Contains(saved.Data!.Warnings, "This person has said they can't work Tuesdays, 17:00–23:00.");
    }

    [TestMethod]
    public async Task RotaSaveShift_OutsideTheirUnavailableTimes_NoWarning()
    {
        World w = await SeedAsync();
        await GetService(w.Options).SaveAsync(w.AmyScope, Window(w.Amy, DayOfWeek.Tuesday, 17, 23), w.Amy.Id);

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), new RecordingNotificationDispatcher(), NullLogger<RotaService>.Instance);

        DateOnly tuesday = new(2026, 10, 6);
        Shift shift = new()
        {
            LocationId = w.Ipswich.Id,
            UserId = w.Amy.Id,
            StartUtc = RotaTime.ToUtc(tuesday, new TimeOnly(9, 0)),
            EndUtc = RotaTime.ToUtc(tuesday, new TimeOnly(17, 0)),
            CreateByUserId = ManagerId
        };

        Result<ShiftSaveResult> saved = await rota.SaveShiftAsync(w.ManagerScope, shift, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.IsFalse(saved.Data!.Warnings.Any(x => x.Contains("can't work")));
    }
}
