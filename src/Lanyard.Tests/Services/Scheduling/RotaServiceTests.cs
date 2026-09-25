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
public class RotaServiceTests
{
    // Week of Monday 5 October 2026 - British Summer Time, so every test also exercises the
    // local/UTC split (09:00 local is 08:00 UTC).
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly Sunday = Monday.AddDays(6);
    private const string Manager = "manager-user";

    private static RotaService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new RotaService(factory, new ContractRequirementService(factory), new RecordingNotificationDispatcher(), NullLogger<RotaService>.Instance);
    }

    private static Shift ShiftFor(Location location, UserProfile user, DateOnly day, int startHour, int endHour, int breakMinutes = 0, Guid? positionId = null) => new()
    {
        LocationId = location.Id,
        UserId = user.Id,
        StaffPositionId = positionId,
        StartUtc = RotaTime.ToUtc(day, new TimeOnly(startHour, 0)),
        EndUtc = RotaTime.ToUtc(day, new TimeOnly(endHour, 0)),
        BreakMinutes = breakMinutes,
        CreateByUserId = Manager
    };

    private static async Task<Shift> SeedShiftAsync(DbContextOptions<ApplicationDbContext> options, Shift shift, DateTime? publishedUtc = null)
    {
        await using ApplicationDbContext ctx = new(options);

        shift.Id = Guid.NewGuid();
        shift.IsActive = true;
        shift.CreateDate = DateTime.UtcNow.AddDays(-1);
        shift.PublishedDateUtc = publishedUtc;
        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task GivePositionAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user, StaffPosition position)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = position.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private static async Task SetCompanyContractAsync(DbContextOptions<ApplicationDbContext> options, Company company, int? minShifts = null, decimal? minLength = null, decimal? maxHours = null)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.ContractRequirements.Add(new ContractRequirement
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            MinShiftsPerWeek = minShifts,
            MinShiftLengthHours = minLength,
            MaxHoursPerWeek = maxHours,
            UpdateDate = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<Location> AddLocationAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name)
    {
        await using ApplicationDbContext ctx = new(options);
        Location location = new() { CompanyId = company.Id, Name = name, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();
        return location;
    }

    private static async Task AddMembershipAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user, Location location)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = location.Id, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // --- GetRangeViewAsync ---

    [TestMethod]
    public async Task GetRangeViewAsync_RejectsManagerOfAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");

        Result<RotaRangeView> result = await GetService(options).GetRangeViewAsync(SchedulingTestHelpers.ManagerScopeFor(wisbech), ipswich.Id, Monday, Sunday);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_WarnsPositionedMemberWithNoShiftThatWeek()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        await GivePositionAsync(options, ben, csa);
        await SetCompanyContractAsync(options, company, minShifts: 1, minLength: 2);

        Result<RotaRangeView> result = await GetService(options).GetRangeViewAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id, Monday, Sunday);

        Assert.IsTrue(result.IsSuccess, result.Error);
        RotaStaffRow row = result.Data!.Rows.Single();
        Assert.AreEqual(ben.Id, row.User.Id);
        Assert.AreEqual(ContractWarningKind.BelowMinShifts, row.Warnings.Single().Kind);
        Assert.AreEqual("CSA", row.PrimaryPosition!.StaffPosition!.Name);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_ExcludesMembersWithoutPositionUnlessRequested()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        await SchedulingTestHelpers.SeedUserAsync(options, location, "Noposition");
        RotaService service = GetService(options);

        Result<RotaRangeView> positionedOnly = await service.GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday);
        Result<RotaRangeView> everyone = await service.GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, includeAllMembers: true);

        Assert.AreEqual(0, positionedOnly.Data!.Rows.Count);
        Assert.AreEqual(1, everyone.Data!.Rows.Count);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_CountsHoursAtOtherLocationsButOnlyShowsThisLocationsShifts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich);
        await AddMembershipAsync(options, ben, wisbech);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        await GivePositionAsync(options, ben, csa);
        await SetCompanyContractAsync(options, company, maxHours: 10);

        await SeedShiftAsync(options, ShiftFor(ipswich, ben, Monday, 9, 15));
        await SeedShiftAsync(options, ShiftFor(wisbech, ben, Monday.AddDays(1), 9, 15));

        Result<RotaRangeView> result = await GetService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, ipswich.Id, Monday, Sunday);

        RotaStaffRow row = result.Data!.Rows.Single();
        Assert.AreEqual(1, row.Shifts.Count);
        Assert.AreEqual(12m, row.HoursByWeek[Monday]);
        Assert.AreEqual(ContractWarningKind.AboveMaxHours, row.Warnings.Single().Kind);
    }

    [TestMethod]
    public async Task GetRangeViewAsync_EvaluatesWholeWeekAtEdgeOfMonth()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");
        await GivePositionAsync(options, ben, csa);
        await SetCompanyContractAsync(options, company, minShifts: 1, minLength: 2);

        // October 2026 starts on a Thursday; the week of Mon 28 Sep is satisfied by a Tuesday
        // shift in September, which is outside the month but inside the week.
        await SeedShiftAsync(options, ShiftFor(location, ben, new DateOnly(2026, 9, 29), 9, 13));

        Result<RotaRangeView> result = await GetService(options).GetRangeViewAsync(
            SchedulingTestHelpers.AdminScope, location.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        RotaStaffRow row = result.Data!.Rows.Single();
        Assert.IsFalse(row.Warnings.Any(w => w.WeekStart == new DateOnly(2026, 9, 28)));
        Assert.IsTrue(row.Warnings.Any(w => w.WeekStart == Monday));
    }

    [TestMethod]
    public async Task GetRangeViewAsync_CountsUnpublishedChangesInRange()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");

        await SeedShiftAsync(options, ShiftFor(location, ann, Monday, 9, 17));
        await SeedShiftAsync(options, ShiftFor(location, ann, Monday.AddDays(1), 9, 17));
        await SeedShiftAsync(options, ShiftFor(location, bob, Monday, 9, 17), publishedUtc: DateTime.UtcNow);
        await SeedShiftAsync(options, ShiftFor(location, bob, Monday.AddDays(7), 9, 17));

        Result<RotaRangeView> result = await GetService(options).GetRangeViewAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday);

        Assert.AreEqual(2, result.Data!.UnpublishedChangeCount);
        Assert.AreEqual(1, result.Data.PeopleWithUnpublishedChanges);
    }

    // --- SaveShiftAsync ---

    [TestMethod]
    public async Task SaveShiftAsync_CreatesDraft()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), ShiftFor(location, ben, Monday, 9, 17, breakMinutes: 30), Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ShiftState.Draft, result.Data!.Shift.GetState());
        Assert.AreEqual(7.5m, result.Data.Shift.PaidHours);

        await using ApplicationDbContext ctx = new(options);
        Shift saved = await ctx.Shifts.SingleAsync();
        Assert.AreEqual(new DateTime(2026, 10, 5, 8, 0, 0), saved.StartUtc);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsEndBeforeStart()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(location, ben, Monday, 17, 9), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsBreakAsLongAsShift()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(location, ben, Monday, 9, 10, breakMinutes: 60), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveShiftAsync_AllowsOvernightShift()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Shift overnight = ShiftFor(location, ben, Monday, 22, 23);
        overnight.EndUtc = RotaTime.ToUtc(Monday.AddDays(1), new TimeOnly(2, 0));

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, overnight, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(4m, result.Data!.Shift.PaidHours);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsUserNotAtLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, wisbech);

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(ipswich, ben, Monday, 9, 17), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsManagerOfAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich);

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.ManagerScopeFor(wisbech), ShiftFor(ipswich, ben, Monday, 9, 17), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsOverlapIncludingAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, ipswich);
        await AddMembershipAsync(options, ben, wisbech);
        await SeedShiftAsync(options, ShiftFor(wisbech, ben, Monday, 12, 18));

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(ipswich, ben, Monday, 9, 13), Manager);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Wisbech");
    }

    [TestMethod]
    public async Task SaveShiftAsync_AllowsBackToBackShifts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 13));

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(location, ben, Monday, 13, 17), Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task SaveShiftAsync_WarnsWhenPositionNotHeld()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(location, ben, Monday, 9, 17, positionId: supervisor.Id), Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Warnings.Single(), "Supervisor");
    }

    [TestMethod]
    public async Task SaveShiftAsync_ResavingUnchangedPublishedShiftDoesNotMarkItChanged()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift published = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));

        Shift resave = ShiftFor(location, ben, Monday, 9, 17);
        resave.Id = published.Id;

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, resave, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ShiftState.Published, result.Data!.Shift.GetState());
    }

    [TestMethod]
    public async Task SaveShiftAsync_EditingPublishedShiftMarksItChanged()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift published = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));

        Shift edit = ShiftFor(location, ben, Monday, 10, 17);
        edit.Id = published.Id;

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, edit, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ShiftState.Changed, result.Data!.Shift.GetState());
    }

    [TestMethod]
    public async Task SaveShiftAsync_ReassigningPublishedShiftRemovesItAndCreatesDraft()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        Shift published = await SeedShiftAsync(options, ShiftFor(location, ann, Monday, 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));

        Shift reassign = ShiftFor(location, bob, Monday, 9, 17);
        reassign.Id = published.Id;

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, reassign, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreNotEqual(published.Id, result.Data!.Shift.Id);

        await using ApplicationDbContext ctx = new(options);
        Shift original = await ctx.Shifts.SingleAsync(x => x.Id == published.Id);
        Assert.AreEqual(ShiftState.RemovedPendingNotice, original.GetState());
        Assert.AreEqual(ann.Id, original.UserId);
        Assert.AreEqual(ShiftState.Draft, (await ctx.Shifts.SingleAsync(x => x.UserId == bob.Id)).GetState());
    }

    // --- DeleteShiftAsync ---

    [TestMethod]
    public async Task DeleteShiftAsync_PublishedShiftAwaitsNotice()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift published = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));

        Result<bool> result = await GetService(options).DeleteShiftAsync(SchedulingTestHelpers.AdminScope, published.Id, Manager);

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(ShiftState.RemovedPendingNotice, (await ctx.Shifts.SingleAsync()).GetState());
    }

    [TestMethod]
    public async Task DeleteShiftAsync_DraftDisappearsWithoutNotice()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift draft = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));

        await GetService(options).DeleteShiftAsync(SchedulingTestHelpers.AdminScope, draft.Id, Manager);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(ShiftState.Removed, (await ctx.Shifts.SingleAsync()).GetState());
    }

    // --- PublishRangeAsync ---

    [TestMethod]
    public async Task PublishRangeAsync_ReportsNewChangedAndRemovedPerPerson()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        RotaService service = GetService(options);
        LocationScope scope = SchedulingTestHelpers.AdminScope;

        await SeedShiftAsync(options, ShiftFor(location, ann, Monday, 9, 17));
        Shift toChange = await SeedShiftAsync(options, ShiftFor(location, bob, Monday, 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));
        Shift toRemove = await SeedShiftAsync(options, ShiftFor(location, bob, Monday.AddDays(1), 9, 17), publishedUtc: DateTime.UtcNow.AddHours(-1));

        Shift edit = ShiftFor(location, bob, Monday, 12, 17);
        edit.Id = toChange.Id;
        await service.SaveShiftAsync(scope, edit, Manager);
        await service.DeleteShiftAsync(scope, toRemove.Id, Manager);

        Result<PublishResult> result = await service.PublishRangeAsync(scope, location.Id, Monday, Sunday, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.PeopleAffected);
        Assert.AreEqual(3, result.Data.ShiftCount);

        PublishedChange annChange = result.Data.Changes.Single(x => x.UserId == ann.Id);
        PublishedChange bobChange = result.Data.Changes.Single(x => x.UserId == bob.Id);
        Assert.AreEqual(1, annChange.New.Count);
        Assert.AreEqual(1, bobChange.Changed.Count);
        Assert.AreEqual(1, bobChange.Removed.Count);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsTrue(await ctx.Shifts.Where(x => x.IsActive).AllAsync(x => x.PublishedDateUtc != null));
        Assert.IsFalse(await ctx.Shifts.AnyAsync(x => x.RemovalPending));
    }

    [TestMethod]
    public async Task PublishRangeAsync_LeavesShiftsOutsideRangeAsDrafts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));
        Shift nextWeek = await SeedShiftAsync(options, ShiftFor(location, ben, Monday.AddDays(7), 9, 17));

        Result<PublishResult> result = await GetService(options).PublishRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, Manager);

        Assert.AreEqual(1, result.Data!.ShiftCount);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsNull((await ctx.Shifts.SingleAsync(x => x.Id == nextWeek.Id)).PublishedDateUtc);
    }

    [TestMethod]
    public async Task PublishRangeAsync_SecondPublishHasNothingToDo()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));
        RotaService service = GetService(options);

        await service.PublishRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, Manager);
        Result<PublishResult> second = await service.PublishRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, Manager);

        Assert.AreEqual(0, second.Data!.ShiftCount);
    }

    [TestMethod]
    public async Task PublishRangeAsync_RejectsManagerOfAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");

        Result<PublishResult> result = await GetService(options).PublishRangeAsync(SchedulingTestHelpers.ManagerScopeFor(wisbech), ipswich.Id, Monday, Sunday, Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    // --- CopyRangeAsync ---

    [TestMethod]
    public async Task CopyRangeAsync_CopiesAsDraftsKeepingWallClockTimesAcrossClockChange()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17), publishedUtc: DateTime.UtcNow);

        // 4 weeks later is 2 November - after the clocks go back - and must still be 09:00 local.
        DateOnly target = Monday.AddDays(28);

        Result<CopyRangeResult> result = await GetService(options).CopyRangeAsync(
            SchedulingTestHelpers.AdminScope, location.Id, target, target.AddDays(6), 28, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Copied);

        await using ApplicationDbContext ctx = new(options);
        Shift copy = await ctx.Shifts.OrderByDescending(x => x.StartUtc).FirstAsync();
        Assert.AreEqual(ShiftState.Draft, copy.GetState());
        Assert.AreEqual(target, RotaTime.LocalDate(copy.StartUtc));
        Assert.AreEqual(new TimeOnly(9, 0), RotaTime.LocalTime(copy.StartUtc));
        Assert.AreEqual(new TimeOnly(17, 0), RotaTime.LocalTime(copy.EndUtc));
    }

    [TestMethod]
    public async Task CopyRangeAsync_SkipsOverlapsAndLeavers()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ann = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        UserProfile leaver = await SchedulingTestHelpers.SeedUserAsync(options, location, "Lee");

        await SeedShiftAsync(options, ShiftFor(location, ann, Monday, 9, 17));
        await SeedShiftAsync(options, ShiftFor(location, bob, Monday, 9, 17));
        await SeedShiftAsync(options, ShiftFor(location, leaver, Monday, 9, 17));
        await SeedShiftAsync(options, ShiftFor(location, bob, Monday.AddDays(7), 12, 14));

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships.Where(x => x.UserId == leaver.Id));
            await ctx.SaveChangesAsync();
        }

        Result<CopyRangeResult> result = await GetService(options).CopyRangeAsync(
            SchedulingTestHelpers.AdminScope, location.Id, Monday.AddDays(7), Sunday.AddDays(7), 7, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Copied);
        Assert.AreEqual(2, result.Data.Skipped.Count);
    }

    [TestMethod]
    public async Task CopyRangeAsync_TwiceDoesNotDuplicate()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));
        RotaService service = GetService(options);

        await service.CopyRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday.AddDays(7), Sunday.AddDays(7), 7, Manager);
        Result<CopyRangeResult> second = await service.CopyRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday.AddDays(7), Sunday.AddDays(7), 7, Manager);

        Assert.AreEqual(0, second.Data!.Copied);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(2, await ctx.Shifts.CountAsync());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(10)]
    [DataRow(-7)]
    public async Task CopyRangeAsync_RejectsOffsetThatIsNotWholeWeeks(int offset)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<CopyRangeResult> result = await GetService(options).CopyRangeAsync(
            SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, offset, Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    // --- GetShiftsForUserAsync ---

    [TestMethod]
    public async Task GetShiftsForUserAsync_ReturnsOnlyPublishedActiveShifts()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Shift published = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17), publishedUtc: DateTime.UtcNow);
        await SeedShiftAsync(options, ShiftFor(location, ben, Monday.AddDays(1), 9, 17));
        Shift removed = await SeedShiftAsync(options, ShiftFor(location, ben, Monday.AddDays(2), 9, 17), publishedUtc: DateTime.UtcNow);

        await using (ApplicationDbContext ctx = new(options))
        {
            Shift toRemove = await ctx.Shifts.SingleAsync(x => x.Id == removed.Id);
            toRemove.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<List<Shift>> result = await GetService(options).GetShiftsForUserAsync(
            ben.Id, RotaTime.StartOfDayUtc(Monday), RotaTime.StartOfDayUtc(Monday.AddDays(7)));

        Assert.AreEqual(published.Id, result.Data!.Single().Id);
        Assert.AreEqual(location.Name, result.Data[0].Location!.Name);
    }

    // --- Review fixes ---

    [TestMethod]
    public async Task EveryRotaAction_RejectsStaffWithoutManagerRoleAtTheirOwnLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift existing = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));
        RotaService service = GetService(options);
        LocationScope staff = SchedulingTestHelpers.StaffScopeFor(location);

        Assert.IsFalse((await service.GetRangeViewAsync(staff, location.Id, Monday, Sunday)).IsSuccess);
        Assert.IsFalse((await service.SaveShiftAsync(staff, ShiftFor(location, ben, Monday.AddDays(1), 9, 17), ben.Id)).IsSuccess);
        Assert.IsFalse((await service.DeleteShiftAsync(staff, existing.Id, ben.Id)).IsSuccess);
        Assert.IsFalse((await service.CopyRangeAsync(staff, location.Id, Monday.AddDays(7), Sunday.AddDays(7), 7, ben.Id)).IsSuccess);
        Assert.IsFalse((await service.PublishRangeAsync(staff, location.Id, Monday, Sunday, ben.Id)).IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.Shifts.CountAsync());
        Assert.IsNull((await ctx.Shifts.SingleAsync()).PublishedDateUtc);
    }

    [TestMethod]
    public async Task SaveShiftAsync_AllowsEditingALeaversExistingShift()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile leaver = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift shift = await SeedShiftAsync(options, ShiftFor(location, leaver, Monday, 9, 17));

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships.Where(x => x.UserId == leaver.Id));
            await ctx.SaveChangesAsync();
        }

        Shift edit = ShiftFor(location, leaver, Monday, 9, 15);
        edit.Id = shift.Id;

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, edit, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(6m, result.Data!.Shift.PaidHours);
    }

    [TestMethod]
    public async Task SaveShiftAsync_KeepsArchivedPositionWhenEditingOtherFields()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");
        Shift shift = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17, positionId: supervisor.Id));

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.StaffPositions.SingleAsync()).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Shift edit = ShiftFor(location, ben, Monday, 9, 17, positionId: supervisor.Id);
        edit.Id = shift.Id;
        edit.Notes = "Covering the party room";

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(SchedulingTestHelpers.AdminScope, edit, Manager);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(supervisor.Id, result.Data!.Shift.StaffPositionId);
        Assert.AreEqual(0, result.Data.Warnings.Count);
    }

    [TestMethod]
    public async Task SaveShiftAsync_RejectsNewlyChosenArchivedPosition()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        StaffPosition supervisor = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Supervisor");

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.StaffPositions.SingleAsync()).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<ShiftSaveResult> result = await GetService(options).SaveShiftAsync(
            SchedulingTestHelpers.AdminScope, ShiftFor(location, ben, Monday, 9, 17, positionId: supervisor.Id), Manager);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task PublishRangeAsync_RecordsWhereTheShiftStartedWhenPublished()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        Shift draft = await SeedShiftAsync(options, ShiftFor(location, ben, Monday, 9, 17));

        await GetService(options).PublishRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Monday, Sunday, Manager);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(draft.StartUtc, (await ctx.Shifts.SingleAsync()).PublishedStartUtc);
    }

    [TestMethod]
    public async Task PublishRangeAsync_IncludesPublishedShiftMovedIntoAnotherWeek()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location);
        RotaService service = GetService(options);
        LocationScope scope = SchedulingTestHelpers.AdminScope;

        // Friday of week 1, published...
        Shift friday = await SeedShiftAsync(options, ShiftFor(location, ben, Monday.AddDays(4), 9, 17));
        await service.PublishRangeAsync(scope, location.Id, Monday, Sunday, Manager);

        // ...then moved to Monday of week 2.
        Shift moved = ShiftFor(location, ben, Monday.AddDays(7), 9, 17);
        moved.Id = friday.Id;
        await service.SaveShiftAsync(scope, moved, Manager);

        Result<RotaRangeView> weekOne = await service.GetRangeViewAsync(scope, location.Id, Monday, Sunday);
        Assert.AreEqual(1, weekOne.Data!.UnpublishedChangeCount);

        Result<PublishResult> result = await service.PublishRangeAsync(scope, location.Id, Monday, Sunday, Manager);

        Assert.AreEqual(1, result.Data!.Changes.Single().Changed.Count);

        Result<RotaRangeView> weekTwo = await service.GetRangeViewAsync(scope, location.Id, Monday.AddDays(7), Sunday.AddDays(7));
        Assert.AreEqual(0, weekTwo.Data!.UnpublishedChangeCount);
    }
}
