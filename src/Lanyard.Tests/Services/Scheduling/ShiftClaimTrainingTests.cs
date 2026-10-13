using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

// Training a position requires (StaffPositionRequiredCourse): staff without it can't pick up or
// swap into that position's shifts, aren't told about them, and see the course to take; a manager
// rostering them directly only gets a warning.
[TestClass]
public class ShiftClaimTrainingTests
{
    private static readonly DateTime Now = DateTime.UtcNow.Date.AddHours(11);
    private static readonly DateTime Day = Now.Date.AddDays(10);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Location,
        StaffPosition Marshal,
        UserProfile Manager,
        UserProfile Amy,
        UserProfile Tom,
        UserProfile Priya,
        RecordingNotificationDispatcher Notifications,
        ShiftClaimService Service)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Location);
    }

    // A location with a manager and three Marshals (Amy, Tom, Priya). Nothing is required yet.
    private static async Task<World> SeedAsync(bool claimsNeedApproval = true)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition marshal = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Marshal");

        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, location, "Priya");

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (UserProfile user in new[] { amy, tom, priya })
            {
                ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = marshal.Id, IsPrimary = true, CreateDate = Now });
            }

            ApplicationRole role = new() { Id = Guid.NewGuid().ToString(), Name = "Manager", NormalizedName = "MANAGER", CreatedByUserId = "seed", IsActive = true };
            ctx.Roles.Add(role);
            ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = manager.Id, RoleId = role.Id });

            if (!claimsNeedApproval)
            {
                ctx.LocationSchedulingSettings.Add(new LocationSchedulingSettings { Id = Guid.NewGuid(), LocationId = location.Id, ClaimsNeedApproval = false });
            }

            await ctx.SaveChangesAsync();
        }

        RecordingNotificationDispatcher notifications = new();
        ShiftClaimService service = new(SchedulingTestHelpers.GetFactory(options), notifications, new ShiftClaimEventBus(),
            new TestClock(Now), NullLogger<ShiftClaimService>.Instance);

        return new World(options, company, location, marshal, manager, amy, tom, priya, notifications, service);
    }

    private static async Task<Shift> ShiftAsync(World w, string? userId, int startHour = 9, int hours = 8, int dayOffset = 0, bool published = true, Guid? positionId = null, int? locationId = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        DateTime start = Day.AddDays(dayOffset).AddHours(startHour);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = locationId ?? w.Location.Id,
            UserId = userId,
            StaffPositionId = positionId ?? w.Marshal.Id,
            StartUtc = start,
            EndUtc = start.AddHours(hours),
            IsActive = true,
            CreateDate = Now.AddDays(-5),
            CreateByUserId = w.Manager.Id,
            PublishedDateUtc = published ? Now.AddDays(-4) : null,
            PublishedStartUtc = published ? start : null
        };

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    // A course at the world's location unless locationId says otherwise (noLocation for one with
    // no location at all).
    private static async Task<Course> CourseAsync(World w, string name, int? recurrenceMonths = null, bool isActive = true, bool isShared = false, int? locationId = null, bool noLocation = false, int? companyId = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        Course course = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId ?? w.Company.Id,
            Name = name,
            RecurrenceMonths = recurrenceMonths,
            IsActive = isActive,
            IsShared = isShared,
            LocationId = noLocation ? null : locationId ?? w.Location.Id
        };

        ctx.Courses.Add(course);
        await ctx.SaveChangesAsync();

        return course;
    }

    private static async Task RequireAsync(World w, Course course, StaffPosition? position = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        ctx.StaffPositionRequiredCourses.Add(new StaffPositionRequiredCourse
        {
            Id = Guid.NewGuid(),
            StaffPositionId = (position ?? w.Marshal).Id,
            CourseId = course.Id,
            CreateDate = Now
        });

        await ctx.SaveChangesAsync();
    }

    // completedUtc null = assigned but not finished.
    private static async Task<CourseAssignment> AssignAsync(World w, Course course, string userId, DateTime? completedUtc, bool isActive = true, DateTime? assignedUtc = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        CourseAssignment assignment = new()
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            UserId = userId,
            AssignedDate = assignedUtc ?? (completedUtc ?? Now).AddDays(-7),
            CompletedDate = completedUtc,
            IsActive = isActive,
            LocationId = w.Location.Id
        };

        ctx.CourseAssignments.Add(assignment);
        await ctx.SaveChangesAsync();

        return assignment;
    }

    private static List<string> Told(World w, NotificationTopic topic) =>
        w.Notifications.Jobs.Where(x => x.Topic == topic).Select(x => x.UserId).ToList();

    private static RotaService Rota(World w)
    {
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        return new RotaService(factory, new ContractRequirementService(factory), w.Notifications, NullLogger<RotaService>.Instance);
    }

    // ---- Picking up ------------------------------------------------------------------------

    [TestMethod]
    public async Task PickUp_RefusedWithoutRequiredTraining_NamingTheCourse()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        Shift open = await ShiftAsync(w, null);

        Result<ShiftClaim> result = await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("You need Laser Arena Safety training to work as Marshal.", result.Error);
    }

    [TestMethod]
    public async Task PickUp_ListsEveryMissingCourse()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        Course firstAid = await CourseAsync(w, "First Aid");
        await RequireAsync(w, arena);
        await RequireAsync(w, firstAid);
        await AssignAsync(w, firstAid, w.Tom.Id, Now.AddDays(-30));
        Shift open = await ShiftAsync(w, null);

        string? amy = (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Error;
        string? tom = (await w.Service.PickUpAsync(w.Tom.Id, open.Id, null)).Error;

        StringAssert.Contains(amy, "and");
        StringAssert.Contains(amy, "Laser Arena Safety");
        StringAssert.Contains(amy, "First Aid");
        Assert.AreEqual("You need Laser Arena Safety training to work as Marshal.", tom);
    }

    [TestMethod]
    public async Task PickUp_AllowedOnceTheCourseIsPassed()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30));
        Shift open = await ShiftAsync(w, null);

        Result<ShiftClaim> result = await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task PickUp_StartedButUnfinishedCourseDoesNotCount()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, completedUtc: null);
        Shift open = await ShiftAsync(w, null);

        Assert.IsFalse((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task PickUp_UnassignedCompletionDoesNotCount()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30), isActive: false);
        Shift open = await ShiftAsync(w, null);

        Assert.IsFalse((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task PickUp_RecurringCourse_RefusedWhenItRunsOutBeforeTheShift()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety", recurrenceMonths: 12);
        await RequireAsync(w, arena);

        // Still in date today, but runs out the day before the shift.
        await AssignAsync(w, arena, w.Amy.Id, Day.AddMonths(-12).AddDays(-1));
        // Runs out the day after the shift.
        await AssignAsync(w, arena, w.Tom.Id, Day.AddMonths(-12).AddDays(1));
        Shift open = await ShiftAsync(w, null);

        Result<ShiftClaim> amy = await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);
        Result<ShiftClaim> tom = await w.Service.PickUpAsync(w.Tom.Id, open.Id, null);

        Assert.AreEqual("Your Laser Arena Safety training runs out before this shift.", amy.Error);
        Assert.IsTrue(tom.IsSuccess, tom.Error);
    }

    [TestMethod]
    public async Task PickUp_RecurringCourse_LatestCycleCounts()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety", recurrenceMonths: 12);
        await RequireAsync(w, arena);

        // An old cycle that has run out, and the retake passed last week.
        await AssignAsync(w, arena, w.Amy.Id, Now.AddMonths(-14));
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-7));
        Shift open = await ShiftAsync(w, null);

        Assert.IsTrue((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task PickUp_ArchivedCourseIsNotEnforced()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety", isActive: false);
        await RequireAsync(w, arena);
        Shift open = await ShiftAsync(w, null);

        Assert.IsTrue((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task PickUp_NotEnforcedWhenTheCompanyHasTrainingSwitchedOff()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Training, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Shift open = await ShiftAsync(w, null);

        Assert.IsTrue((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task PickUp_AnotherLocationsOwnCourseOnlyGatesShiftsThere()
    {
        World w = await SeedAsync();
        Location harbourside;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            harbourside = new Location { CompanyId = w.Company.Id, Name = "Harbourside", IsActive = true };
            ctx.Locations.Add(harbourside);
            await ctx.SaveChangesAsync();
        }

        // Harbourside's own course, and a shared one from Harbourside.
        Course harbourOnly = await CourseAsync(w, "Harbourside Arena", locationId: harbourside.Id);
        await RequireAsync(w, harbourOnly);
        Shift open = await ShiftAsync(w, null);

        Assert.IsTrue((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess, "Not taken here, so not required here");

        Course shared = await CourseAsync(w, "Shared Safety", locationId: harbourside.Id, isShared: true);
        await RequireAsync(w, shared);
        Shift another = await ShiftAsync(w, null, dayOffset: 1);

        StringAssert.Contains((await w.Service.PickUpAsync(w.Tom.Id, another.Id, null)).Error, "Shared Safety");
    }

    [TestMethod]
    public async Task PickUp_WrongPositionIsReportedBeforeTraining()
    {
        World w = await SeedAsync();
        StaffPosition desk = await SchedulingTestHelpers.SeedPositionAsync(w.Options, w.Company, "Front desk");
        Course till = await CourseAsync(w, "Till Training");
        await RequireAsync(w, till, desk);
        Shift open = await ShiftAsync(w, null, positionId: desk.Id);

        Assert.AreEqual("You don't work as Front desk.", (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Error);
    }

    [TestMethod]
    public async Task PickUp_TrainingIsReportedBeforeAClash()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        Shift open = await ShiftAsync(w, null);
        await ShiftAsync(w, w.Amy.Id, startHour: 12);

        StringAssert.Contains((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Error, "Laser Arena Safety");
    }

    [TestMethod]
    public async Task PickUp_ShiftWithNoPositionNeedsNoTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        Shift open = await ShiftAsync(w, null);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Shift tracked = await ctx.Shifts.SingleAsync(x => x.Id == open.Id);
            tracked.StaffPositionId = null;
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).IsSuccess);
    }

    // ---- My Shifts (the batched calendar path) ---------------------------------------------

    [TestMethod]
    public async Task Marketplace_ShowsTheShiftWithTheCourseToTake()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        Course firstAid = await CourseAsync(w, "First Aid", recurrenceMonths: 12);
        await RequireAsync(w, arena);
        await RequireAsync(w, firstAid);
        CourseAssignment amysArena = await AssignAsync(w, arena, w.Amy.Id, completedUtc: null);
        await AssignAsync(w, firstAid, w.Amy.Id, Now.AddMonths(-13));
        await ShiftAsync(w, null);

        ShiftMarketplace market = (await w.Service.GetMarketplaceAsync(w.Amy.Id)).Data!;

        OpenShiftView view = market.OpenShifts.Single();
        Assert.IsNotNull(view.NotEligibleReason);
        Assert.AreEqual(0, market.ActionableCount, "A shift they can't take isn't counted as something to act on");

        TrainingGap arenaGap = view.MissingTraining.Single(x => x.CourseId == arena.Id);
        Assert.AreEqual(amysArena.Id, arenaGap.OpenAssignmentId, "Links to Amy's own assignment");
        Assert.IsFalse(arenaGap.Lapsed);

        TrainingGap firstAidGap = view.MissingTraining.Single(x => x.CourseId == firstAid.Id);
        Assert.IsTrue(firstAidGap.Lapsed);
        Assert.IsNull(firstAidGap.OpenAssignmentId, "No retake started yet");
    }

    [TestMethod]
    public async Task Marketplace_NoTrainingGapsWhenNothingIsRequired()
    {
        World w = await SeedAsync();
        await ShiftAsync(w, null);

        ShiftMarketplace market = (await w.Service.GetMarketplaceAsync(w.Amy.Id)).Data!;

        Assert.AreEqual(0, market.OpenShifts.Single().MissingTraining.Count);
        Assert.AreEqual(1, market.ActionableCount);
    }

    [TestMethod]
    public async Task Marketplace_SwapPairingsUseEachRequestersTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30));
        await AssignAsync(w, arena, w.Tom.Id, Now.AddDays(-30));

        // Tom (trained) and Priya (not) both want to swap; Amy (trained) could give either a shift.
        Shift toms = await ShiftAsync(w, w.Tom.Id);
        Shift priyas = await ShiftAsync(w, w.Priya.Id, dayOffset: 1);
        Shift amys = await ShiftAsync(w, w.Amy.Id, dayOffset: 2);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            // Priya asked before the requirement existed - she couldn't ask now.
            ctx.ShiftClaims.Add(new ShiftClaim { Id = Guid.NewGuid(), ShiftId = priyas.Id, UserId = w.Priya.Id, Kind = ShiftClaimKind.Swap, Status = ShiftClaimStatus.Pending, RequestedUtc = Now });
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await w.Service.RequestSwapAsync(w.Tom.Id, toms.Id, null)).IsSuccess);

        ShiftMarketplace market = (await w.Service.GetMarketplaceAsync(w.Amy.Id)).Data!;

        Assert.AreEqual(amys.Id, market.SwapRequests.Single(x => x.Shift.Id == toms.Id).ShiftsICanOffer.Single().Id);
        Assert.AreEqual(0, market.SwapRequests.Single(x => x.Shift.Id == priyas.Id).ShiftsICanOffer.Count,
            "Priya couldn't take Amy's shift in return");
    }

    // ---- Swaps -----------------------------------------------------------------------------

    [TestMethod]
    public async Task OfferSwap_RefusedWhenEitherSideLacksTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30));
        await AssignAsync(w, arena, w.Tom.Id, Now.AddDays(-30));

        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        Shift priyas = await ShiftAsync(w, w.Priya.Id, dayOffset: 2);

        ShiftClaim amysSwap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;

        // Priya isn't trained, so she can't take Amy's shift.
        StringAssert.Contains((await w.Service.OfferSwapAsync(w.Priya.Id, amysSwap.Id, priyas.Id)).Error, "Laser Arena Safety");

        // Tom can.
        Assert.IsTrue((await w.Service.OfferSwapAsync(w.Tom.Id, amysSwap.Id, toms.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task SwapRequest_OnlyToldToColleaguesWithTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Tom.Id, Now.AddDays(-30));

        Shift amys = await ShiftAsync(w, w.Amy.Id);

        Assert.IsTrue((await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).IsSuccess);

        CollectionAssert.AreEquivalent(new[] { w.Tom.Id }, Told(w, NotificationTopic.SwapRequest));
    }

    // ---- Manager review and announcements --------------------------------------------------

    [TestMethod]
    public async Task Review_ShowsTheTrainingProblem_AndApprovalIsRefused()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);
        ShiftClaim claim = (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Data!;

        // The requirement is added after Amy asked.
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);

        ShiftClaimReview review = (await w.Service.GetReviewAsync(w.ManagerScope, w.Location.Id, w.Manager.Id)).Data!;
        Result<bool> decided = await w.Service.DecideAsync(w.ManagerScope, claim.Id, true, null, w.Manager.Id);

        Assert.AreEqual("They need Laser Arena Safety training to work as Marshal.", review.Pickups.Single().Claims.Single().Problem);
        Assert.IsFalse(decided.IsSuccess);
        StringAssert.Contains(decided.Error, "Laser Arena Safety");
    }

    [TestMethod]
    public async Task Publish_AnnouncesOpenShiftsOnlyToPeopleWithTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30));
        await ShiftAsync(w, null, published: false);

        DateOnly day = RotaTime.LocalDate(Day.AddHours(9));
        Result<PublishResult> published = await Rota(w).PublishRangeAsync(w.ManagerScope, w.Location.Id, day, day, w.Manager.Id);

        Assert.IsTrue(published.IsSuccess, published.Error);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id }, Told(w, NotificationTopic.OpenShift));
    }

    // ---- Rota builder: a warning, not a block ----------------------------------------------

    private static Shift NewShiftFor(World w, UserProfile user, DateTime startUtc) => new()
    {
        LocationId = w.Location.Id,
        UserId = user.Id,
        StaffPositionId = w.Marshal.Id,
        StartUtc = startUtc,
        EndUtc = startUtc.AddHours(6),
        CreateByUserId = w.Manager.Id
    };

    [TestMethod]
    public async Task SaveShift_WarnsButSavesWhenThePersonLacksTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);

        Result<ShiftSaveResult> result = await Rota(w).SaveShiftAsync(w.ManagerScope, NewShiftFor(w, w.Amy, Day.AddHours(9)), w.Manager.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("This person hasn't done Laser Arena Safety training.", result.Data!.Warnings.Single());
    }

    [TestMethod]
    public async Task SaveShift_WarnsWhenRecurringTrainingRunsOutBeforeTheShift()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety", recurrenceMonths: 12);
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Day.AddMonths(-12).AddDays(-1));

        Result<ShiftSaveResult> result = await Rota(w).SaveShiftAsync(w.ManagerScope, NewShiftFor(w, w.Amy, Day.AddHours(9)), w.Manager.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("This person's Laser Arena Safety training runs out before this shift.", result.Data!.Warnings.Single());
    }

    [TestMethod]
    public async Task SaveShift_NoTrainingWarningWhenCurrent()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Amy.Id, Now.AddDays(-30));

        Result<ShiftSaveResult> result = await Rota(w).SaveShiftAsync(w.ManagerScope, NewShiftFor(w, w.Amy, Day.AddHours(9)), w.Manager.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Warnings.Count);
    }

    [TestMethod]
    public async Task SaveShift_OnlyThePositionWarningWhenTheyDontHoldIt()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);

        Result<ShiftSaveResult> result = await Rota(w).SaveShiftAsync(w.ManagerScope, NewShiftFor(w, w.Manager, Day.AddHours(9)), w.Manager.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("This person doesn't normally work as Marshal.", result.Data!.Warnings.Single());
    }

    // ---- Rota badges and the publish warning -----------------------------------------------

    [TestMethod]
    public async Task RotaView_BadgesUpcomingShiftsOfPeopleMissingTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Tom.Id, Now.AddDays(-30));

        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        Shift amysPast = await ShiftAsync(w, w.Amy.Id, dayOffset: -20);
        Shift open = await ShiftAsync(w, null, dayOffset: 2);

        DateOnly from = RotaTime.LocalDate(Day.AddDays(-21));
        DateOnly to = RotaTime.LocalDate(Day.AddDays(3));
        Result<RotaRangeView> view = await Rota(w).GetRangeViewAsync(w.ManagerScope, w.Location.Id, from, to);

        Assert.IsTrue(view.IsSuccess, view.Error);
        Assert.AreEqual("Laser Arena Safety", view.Data!.TrainingGapsFor(amys).Single().CourseName);
        Assert.AreEqual(0, view.Data.TrainingGapsFor(toms).Count, "Tom has passed it");
        Assert.AreEqual(0, view.Data.TrainingGapsFor(amysPast).Count, "Past shifts aren't badged");
        Assert.AreEqual(0, view.Data.TrainingGapsFor(open).Count, "Nobody on it");
        Assert.AreEqual(1, view.Data.TrainingGapsByShift.Count);
    }

    [TestMethod]
    public async Task RotaView_NoBadgesWhenNothingIsRequired()
    {
        World w = await SeedAsync();
        await ShiftAsync(w, w.Amy.Id);

        DateOnly day = RotaTime.LocalDate(Day.AddHours(9));
        Result<RotaRangeView> view = await Rota(w).GetRangeViewAsync(w.ManagerScope, w.Location.Id, day, day);

        Assert.IsTrue(view.IsSuccess, view.Error);
        Assert.AreEqual(0, view.Data!.TrainingGapsByShift.Count);
    }

    [TestMethod]
    public async Task Publish_CountsShiftsForPeopleMissingTheTraining()
    {
        World w = await SeedAsync();
        Course arena = await CourseAsync(w, "Laser Arena Safety");
        await RequireAsync(w, arena);
        await AssignAsync(w, arena, w.Tom.Id, Now.AddDays(-30));

        await ShiftAsync(w, w.Amy.Id, published: false);
        await ShiftAsync(w, w.Priya.Id, startHour: 12, published: false);
        await ShiftAsync(w, w.Tom.Id, startHour: 14, published: false);

        DateOnly day = RotaTime.LocalDate(Day.AddHours(9));
        Result<PublishResult> published = await Rota(w).PublishRangeAsync(w.ManagerScope, w.Location.Id, day, day, w.Manager.Id);

        Assert.IsTrue(published.IsSuccess, published.Error);
        Assert.AreEqual(3, published.Data!.ShiftCount, "Published regardless");
        Assert.AreEqual(2, published.Data.ShiftsMissingTraining, "Amy and Priya");
    }
}
