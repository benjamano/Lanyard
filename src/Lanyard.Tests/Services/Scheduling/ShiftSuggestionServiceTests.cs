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

// The ranking itself is covered by ShiftSuggestionRulesTests; these check what the service feeds
// it - and above all that only the shift's own location's staff are ever considered.
[TestClass]
public class ShiftSuggestionServiceTests
{
    // Week of Monday 5 October 2026 - British Summer Time.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private const string ManagerId = "manager-user";

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Ipswich,
        Location Wisbech,
        StaffPosition Host,
        UserProfile Amy,
        UserProfile Tom,
        UserProfile Priya)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
    }

    // Ipswich has Amy and Tom, both Hosts; Priya is a Host at Wisbech only.
    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Host");

        Location wisbech;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Priya");

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (UserProfile user in new[] { amy, tom, priya })
            {
                ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = host.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
            }

            await ctx.SaveChangesAsync();
        }

        return new World(options, company, ipswich, wisbech, host, amy, tom, priya);
    }

    private static ShiftSuggestionService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        return new ShiftSuggestionService(factory, new ContractRequirementService(factory), NullLogger<ShiftSuggestionService>.Instance);
    }

    // An unsaved open shift on Wednesday 10:00-16:00 at the given location.
    private static Shift OpenShift(Location location, Guid? positionId, int dayOffset = 2, int startHour = 10, int endHour = 16) => new()
    {
        LocationId = location.Id,
        StaffPositionId = positionId,
        StartUtc = RotaTime.ToUtc(Monday.AddDays(dayOffset), new TimeOnly(startHour, 0)),
        EndUtc = RotaTime.ToUtc(Monday.AddDays(dayOffset), new TimeOnly(endHour, 0)),
        CreateByUserId = ManagerId
    };

    private static async Task<Shift> SeedShiftAsync(World w, Location location, string? userId, int dayOffset, int startHour, int endHour, DateTime? publishedUtc = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        Shift shift = OpenShift(location, w.Host.Id, dayOffset, startHour, endHour);
        shift.Id = Guid.NewGuid();
        shift.UserId = userId;
        shift.IsActive = true;
        shift.CreateDate = DateTime.UtcNow.AddDays(-1);
        shift.PublishedDateUtc = publishedUtc;
        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    private static async Task AddTimeOffAsync(World w, UserProfile user, DateOnly day, TimeOffStatus status)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        TimeOffType holiday = new() { Id = Guid.NewGuid(), CompanyId = w.Company.Id, Name = "Paid holiday", IsPaid = true, DeductsFromAllowance = true };
        ctx.TimeOffTypes.Add(holiday);
        ctx.TimeOffRequests.Add(new TimeOffRequest
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TimeOffTypeId = holiday.Id,
            LocationId = w.Ipswich.Id,
            StartDate = day,
            EndDate = day,
            Hours = 8,
            Status = status,
            RequestedDateUtc = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<ShiftSuggestions> SuggestAsync(World w, Shift shift)
    {
        Result<ShiftSuggestions> result = await GetService(w.Options).SuggestAsync(w.ManagerScope, shift);

        Assert.IsTrue(result.IsSuccess, result.Error);
        return result.Data!;
    }

    private static List<string> AllNames(ShiftSuggestions suggestions) =>
        suggestions.Ranked.Concat(suggestions.NotSuggested).Select(x => x.DisplayName).ToList();

    // --- Access ---

    [TestMethod]
    public async Task SuggestAsync_ManagerOfAnotherLocation_Fails()
    {
        World w = await SeedAsync();

        Result<ShiftSuggestions> result = await GetService(w.Options)
            .SuggestAsync(SchedulingTestHelpers.ManagerScopeFor(w.Wisbech), OpenShift(w.Ipswich, w.Host.Id));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "your own location");
    }

    [TestMethod]
    public async Task SuggestAsync_StaffMember_Fails()
    {
        World w = await SeedAsync();

        Result<ShiftSuggestions> result = await GetService(w.Options)
            .SuggestAsync(SchedulingTestHelpers.StaffScopeFor(w.Ipswich), OpenShift(w.Ipswich, w.Host.Id));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SuggestAsync_Admin_Succeeds()
    {
        World w = await SeedAsync();

        Result<ShiftSuggestions> result = await GetService(w.Options).SuggestAsync(SchedulingTestHelpers.AdminScope, OpenShift(w.Ipswich, w.Host.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.Ranked.Count);
    }

    [TestMethod]
    public async Task SuggestAsync_EndBeforeStart_Fails()
    {
        World w = await SeedAsync();
        Shift shift = OpenShift(w.Ipswich, w.Host.Id);
        shift.EndUtc = shift.StartUtc.AddHours(-1);

        Result<ShiftSuggestions> result = await GetService(w.Options).SuggestAsync(w.ManagerScope, shift);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "end after it starts");
    }

    [TestMethod]
    public async Task SuggestAsync_PositionFromAnotherCompany_Fails()
    {
        World w = await SeedAsync();
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");
        StaffPosition foreign = await SchedulingTestHelpers.SeedPositionAsync(w.Options, other, "Host");

        Result<ShiftSuggestions> result = await GetService(w.Options).SuggestAsync(w.ManagerScope, OpenShift(w.Ipswich, foreign.Id));

        Assert.IsFalse(result.IsSuccess);
    }

    // --- Who is considered ---

    [TestMethod]
    public async Task SuggestAsync_OnlyThisLocationsStaff_NeverOtherLocations()
    {
        World w = await SeedAsync();

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        CollectionAssert.AreEquivalent(new List<string> { "Amy Tester", "Tom Tester" }, AllNames(suggestions));
        Assert.IsFalse(suggestions.NotSuggested.Any(x => x.UserId == w.Priya.Id));
    }

    [TestMethod]
    public async Task SuggestAsync_OtherCompanysStaff_NeverConsidered()
    {
        World w = await SeedAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");
        UserProfile stranger = await SchedulingTestHelpers.SeedUserAsync(w.Options, elsewhere, "Stranger");

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.IsFalse(suggestions.Ranked.Concat(suggestions.NotSuggested).Any(x => x.UserId == stranger.Id));
    }

    [TestMethod]
    public async Task SuggestAsync_DeletedUserPlaceholder_Skipped()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.Users.Add(new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted", FirstName = "Deleted" });
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = ApplicationDbContext.SystemDeletedUserPlaceholderId, LocationId = w.Ipswich.Id, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.IsFalse(suggestions.Ranked.Concat(suggestions.NotSuggested).Any(x => x.UserId == ApplicationDbContext.SystemDeletedUserPlaceholderId));
    }

    // --- Exclusions, each with its reason ---

    [TestMethod]
    public async Task SuggestAsync_PositionNotHeld_NotSuggestedWithReason()
    {
        World w = await SeedAsync();
        StaffPosition marshal = await SchedulingTestHelpers.SeedPositionAsync(w.Options, w.Company, "Marshal");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = w.Amy.Id, StaffPositionId = marshal.Id, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, marshal.Id));

        Assert.AreEqual(w.Amy.Id, suggestions.Ranked.Single().UserId);
        Assert.AreEqual("They don't work as Marshal.", suggestions.NotSuggested.Single(x => x.UserId == w.Tom.Id).ExcludedReason);
    }

    [TestMethod]
    public async Task SuggestAsync_ClashAtAnotherLocation_NotSuggested()
    {
        World w = await SeedAsync();
        await SeedShiftAsync(w, w.Wisbech, w.Amy.Id, 2, 12, 18);

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual("They're already working then.", suggestions.NotSuggested.Single(x => x.UserId == w.Amy.Id).ExcludedReason);
    }

    [TestMethod]
    public async Task SuggestAsync_ApprovedTimeOff_NotSuggested()
    {
        World w = await SeedAsync();
        await AddTimeOffAsync(w, w.Amy, Monday.AddDays(2), TimeOffStatus.Approved);

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual("They're booked off that day.", suggestions.NotSuggested.Single(x => x.UserId == w.Amy.Id).ExcludedReason);
    }

    [TestMethod]
    public async Task SuggestAsync_MissingRequiredTraining_NotSuggested()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Course course = new() { Id = Guid.NewGuid(), CompanyId = w.Company.Id, Name = "Fire Safety", IsActive = true, LocationId = w.Ipswich.Id };
            ctx.Courses.Add(course);
            ctx.StaffPositionRequiredCourses.Add(new StaffPositionRequiredCourse { Id = Guid.NewGuid(), StaffPositionId = w.Host.Id, CourseId = course.Id, CreateDate = DateTime.UtcNow });
            ctx.CourseAssignments.Add(new CourseAssignment
            {
                Id = Guid.NewGuid(), CourseId = course.Id, UserId = w.Tom.Id, IsActive = true,
                AssignedDate = DateTime.UtcNow.AddMonths(-2), CompletedDate = DateTime.UtcNow.AddMonths(-1)
            });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual(w.Tom.Id, suggestions.Ranked.Single().UserId);
        StringAssert.Contains(suggestions.NotSuggested.Single(x => x.UserId == w.Amy.Id).ExcludedReason, "Fire Safety");
    }

    // --- What the ranking is fed ---

    [TestMethod]
    public async Task SuggestAsync_HoursAtAnotherLocation_AreCounted()
    {
        World w = await SeedAsync();
        await SeedShiftAsync(w, w.Wisbech, w.Amy.Id, 0, 9, 17);

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        ShiftSuggestion amy = suggestions.Ranked.Single(x => x.UserId == w.Amy.Id);
        Assert.AreEqual(8m, amy.WeekHoursBefore);
        Assert.AreEqual(14m, amy.WeekHoursAfter);
        Assert.AreEqual(w.Tom.Id, suggestions.Ranked[0].UserId, "Tom has fewer hours this week");
    }

    [TestMethod]
    public async Task SuggestAsync_CompanyContractMinimum_RanksPersonWhoNeedsIt()
    {
        World w = await SeedAsync();
        await SeedShiftAsync(w, w.Ipswich, w.Tom.Id, 0, 9, 17);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.ContractRequirements.Add(new ContractRequirement { Id = Guid.NewGuid(), CompanyId = w.Company.Id, MinShiftsPerWeek = 2, UpdateDate = DateTime.UtcNow });
            ctx.ContractRequirements.Add(new ContractRequirement { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, MinShiftsPerWeek = 0, UpdateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual(w.Tom.Id, suggestions.Ranked[0].UserId);
        Assert.IsTrue(suggestions.Ranked[0].Reasons.Any(x => x.Kind == SuggestionReasonKind.NeedsShifts));
    }

    [TestMethod]
    public async Task SuggestAsync_PendingTimeOff_IsACaution()
    {
        World w = await SeedAsync();
        await AddTimeOffAsync(w, w.Amy, Monday.AddDays(2), TimeOffStatus.Pending);

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        ShiftSuggestion amy = suggestions.Ranked.Single(x => x.UserId == w.Amy.Id);
        Assert.AreEqual(2, amy.Rank);
        Assert.AreEqual("Has asked for paid holiday that day (not decided yet)", amy.Cautions.Single().Text);
    }

    [TestMethod]
    public async Task SuggestAsync_SavedOpenShiftWithPendingPickup_ShowsWhoAsked()
    {
        World w = await SeedAsync();
        Shift open = await SeedShiftAsync(w, w.Ipswich, null, 2, 10, 16, publishedUtc: DateTime.UtcNow.AddDays(-1));

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.ShiftClaims.Add(new ShiftClaim { Id = Guid.NewGuid(), ShiftId = open.Id, UserId = w.Tom.Id, Kind = ShiftClaimKind.Pickup, Status = ShiftClaimStatus.Pending, RequestedUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, open);

        Assert.AreEqual(w.Tom.Id, suggestions.Ranked[0].UserId);
        Assert.IsTrue(suggestions.Ranked[0].AskedForShift);
        Assert.AreEqual(0m, suggestions.Ranked[0].WeekHoursBefore, "the open shift itself isn't anyone's hours");
    }

    [TestMethod]
    public async Task SuggestAsync_SavesNothingAndLeavesTheCallersShiftAlone()
    {
        World w = await SeedAsync();
        Shift shift = OpenShift(w.Ipswich, w.Host.Id);

        await SuggestAsync(w, shift);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.Shifts.CountAsync());
        Assert.IsNull(shift.StaffPosition);
        Assert.AreEqual(Guid.Empty, shift.Id);
    }

    // --- Accepting a suggestion ---

    [TestMethod]
    public async Task AcceptingASuggestion_OnPublishedOpenShift_MakesADraftAndTellsNobody()
    {
        World w = await SeedAsync();
        Shift open = await SeedShiftAsync(w, w.Ipswich, null, 2, 10, 16, publishedUtc: DateTime.UtcNow.AddDays(-1));
        ShiftSuggestions suggestions = await SuggestAsync(w, open);

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RecordingNotificationDispatcher notifications = new();
        RotaService rota = new(factory, new ContractRequirementService(factory), notifications, NullLogger<RotaService>.Instance);

        Shift accepted = OpenShift(w.Ipswich, w.Host.Id);
        accepted.Id = open.Id;
        accepted.UserId = suggestions.Ranked[0].UserId;

        Result<ShiftSaveResult> saved = await rota.SaveShiftAsync(w.ManagerScope, accepted, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.IsNull(saved.Data!.Shift.PublishedDateUtc);
        Assert.AreEqual(ShiftState.Draft, saved.Data.Shift.GetState());
        Assert.AreEqual(0, notifications.Jobs.Count);
    }

    // --- Availability ---

    [TestMethod]
    public async Task SuggestAsync_SaidTheyCantWorkThen_NotSuggested()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.StaffAvailabilities.Add(new StaffAvailability
            {
                Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DayOfWeek = DayOfWeek.Wednesday,
                StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0), Kind = AvailabilityKind.Unavailable, IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual(w.Tom.Id, suggestions.Ranked.Single().UserId);
        StringAssert.Contains(suggestions.NotSuggested.Single().ExcludedReason, "can't work Wednesdays, 09:00–12:00");
    }

    [TestMethod]
    public async Task SuggestAsync_AvailabilityAtAnotherCompanyOrRemoved_Ignored()
    {
        World w = await SeedAsync();
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.StaffAvailabilities.Add(new StaffAvailability
            {
                Id = Guid.NewGuid(), CompanyId = other.Id, UserId = w.Amy.Id, DayOfWeek = DayOfWeek.Wednesday,
                StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue, Kind = AvailabilityKind.Unavailable, IsActive = true
            });
            ctx.StaffAvailabilities.Add(new StaffAvailability
            {
                Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DayOfWeek = DayOfWeek.Wednesday,
                StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue, Kind = AvailabilityKind.Unavailable, IsActive = false
            });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual(2, suggestions.Ranked.Count);
        Assert.AreEqual(0, suggestions.NotSuggested.Count);
    }

    [TestMethod]
    public async Task SuggestAsync_PrefersThatTime_RanksFirst()
    {
        World w = await SeedAsync();
        string first = (await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id))).Ranked[0].UserId;
        UserProfile other = first == w.Amy.Id ? w.Tom : w.Amy;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.StaffAvailabilities.Add(new StaffAvailability
            {
                Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = other.Id, DayOfWeek = DayOfWeek.Wednesday,
                StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), Kind = AvailabilityKind.Preferred, IsActive = true
            });
            await ctx.SaveChangesAsync();
        }

        ShiftSuggestions suggestions = await SuggestAsync(w, OpenShift(w.Ipswich, w.Host.Id));

        Assert.AreEqual(other.Id, suggestions.Ranked[0].UserId);
        Assert.IsTrue(suggestions.Ranked[0].Reasons.Any(x => x.Kind == SuggestionReasonKind.PreferredTime));
    }
}
