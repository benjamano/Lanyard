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

// Open shifts, call-offs and swaps: who can ask, what a manager's decision does to the shifts,
// who is told, and how requests are tidied away when shifts change underneath them.
[TestClass]
public class ShiftClaimServiceTests
{
    // Relative to the real clock: RotaService stamps publishes with DateTime.UtcNow, so the shifts
    // must be in the future for it too.
    private static readonly DateTime Now = DateTime.UtcNow.Date.AddHours(11);
    private static readonly DateTime Day = Now.Date.AddDays(10);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Location,
        StaffPosition Csa,
        UserProfile Manager,
        UserProfile Amy,
        UserProfile Tom,
        UserProfile Priya,
        RecordingNotificationDispatcher Notifications,
        ShiftClaimService Service)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Location);
    }

    // A location with a manager and three CSAs (Amy, Tom, Priya).
    private static async Task<World> SeedAsync(bool claimsNeedApproval = true)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition csa = await SchedulingTestHelpers.SeedPositionAsync(options, company, "CSA");

        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, location, "Priya");

        await using (ApplicationDbContext ctx = new(options))
        {
            foreach (UserProfile user in new[] { amy, tom, priya })
            {
                ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = csa.Id, IsPrimary = true, CreateDate = Now });
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

        return new World(options, company, location, csa, manager, amy, tom, priya, notifications, service);
    }

    private static async Task<Shift> ShiftAsync(World w, string? userId, int startHour = 9, int hours = 8, int dayOffset = 0, bool published = true, Guid? positionId = null)
    {
        await using ApplicationDbContext ctx = new(w.Options);

        DateTime start = Day.AddDays(dayOffset).AddHours(startHour);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = w.Location.Id,
            UserId = userId,
            StaffPositionId = positionId ?? w.Csa.Id,
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

    private static async Task<Shift> ReloadAsync(World w, Guid shiftId)
    {
        await using ApplicationDbContext ctx = new(w.Options);
        return await ctx.Shifts.AsNoTracking().SingleAsync(x => x.Id == shiftId);
    }

    private static async Task<ShiftClaim> ClaimAsync(World w, Guid claimId)
    {
        await using ApplicationDbContext ctx = new(w.Options);
        return await ctx.ShiftClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
    }

    private static List<string> Told(World w, NotificationTopic topic) =>
        w.Notifications.Jobs.Where(x => x.Topic == topic).Select(x => x.UserId).ToList();

    // ---- Picking up open shifts ------------------------------------------------------------

    [TestMethod]
    public async Task PickUp_NeedsApproval_WaitsAndTellsManagers()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);

        Result<ShiftClaim> result = await w.Service.PickUpAsync(w.Amy.Id, open.Id, "Happy to");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ShiftClaimStatus.Pending, result.Data!.Status);
        Assert.IsNull((await ReloadAsync(w, open.Id)).UserId);
        CollectionAssert.AreEquivalent(new[] { w.Manager.Id }, Told(w, NotificationTopic.ShiftClaimPending));
    }

    [TestMethod]
    public async Task PickUp_WithoutApproval_FirstComeFirstServed()
    {
        World w = await SeedAsync(claimsNeedApproval: false);
        Shift open = await ShiftAsync(w, null);

        Result<ShiftClaim> amy = await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);
        Result<ShiftClaim> tom = await w.Service.PickUpAsync(w.Tom.Id, open.Id, null);

        Assert.AreEqual(ShiftClaimStatus.Approved, amy.Data!.Status);
        Assert.IsFalse(tom.IsSuccess);

        Shift taken = await ReloadAsync(w, open.Id);
        Assert.AreEqual(w.Amy.Id, taken.UserId);

        // Still counts as published - it isn't an unpublished change waiting for the manager.
        Assert.AreEqual(ShiftState.Published, taken.GetState());
    }

    [TestMethod]
    public async Task PickUp_RefusedWhenTheyCantWorkIt()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);

        // Tom is already working then; the manager holds no CSA position.
        await ShiftAsync(w, w.Tom.Id, startHour: 12);

        Result<ShiftClaim> clash = await w.Service.PickUpAsync(w.Tom.Id, open.Id, null);
        Result<ShiftClaim> wrongPosition = await w.Service.PickUpAsync(w.Manager.Id, open.Id, null);

        StringAssert.Contains(clash.Error, "already working");
        StringAssert.Contains(wrongPosition.Error, "don't work as CSA");
    }

    [TestMethod]
    public async Task PickUp_RefusedOnApprovedTimeOff()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            TimeOffType type = new() { Id = Guid.NewGuid(), CompanyId = w.Company.Id, Name = "Paid holiday", IsPaid = true };
            ctx.TimeOffTypes.Add(type);
            DateOnly day = RotaTime.LocalDate(open.StartUtc);
            ctx.TimeOffRequests.Add(new TimeOffRequest
            {
                Id = Guid.NewGuid(), UserId = w.Amy.Id, TimeOffTypeId = type.Id, LocationId = w.Location.Id,
                StartDate = day, EndDate = day, Hours = 8, Status = TimeOffStatus.Approved, RequestedDateUtc = Now
            });
            await ctx.SaveChangesAsync();
        }

        Result<ShiftClaim> result = await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);

        StringAssert.Contains(result.Error, "booked off");
    }

    [TestMethod]
    public async Task PickUp_DraftOpenShiftIsntAvailable()
    {
        World w = await SeedAsync();
        Shift draft = await ShiftAsync(w, null, published: false);

        Assert.IsFalse((await w.Service.PickUpAsync(w.Amy.Id, draft.Id, null)).IsSuccess);
    }

    [TestMethod]
    public async Task Decide_Pickup_GivesItToOne_AndTellsTheOthers()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);

        ShiftClaim amy = (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Data!;
        ShiftClaim tom = (await w.Service.PickUpAsync(w.Tom.Id, open.Id, null)).Data!;
        w.Notifications.Jobs.Clear();

        Result<bool> decided = await w.Service.DecideAsync(w.ManagerScope, tom.Id, true, null, w.Manager.Id);

        Assert.IsTrue(decided.IsSuccess, decided.Error);
        Assert.AreEqual(w.Tom.Id, (await ReloadAsync(w, open.Id)).UserId);
        Assert.AreEqual(ShiftClaimStatus.Approved, (await ClaimAsync(w, tom.Id)).Status);
        Assert.AreEqual(ShiftClaimStatus.Rejected, (await ClaimAsync(w, amy.Id)).Status);

        List<ShiftClaimDecidedPayload> decisions = w.Notifications.Jobs
            .Where(x => x.Topic == NotificationTopic.ShiftClaimDecided)
            .Select(x => (ShiftClaimDecidedPayload)x.Payload).ToList();

        CollectionAssert.AreEquivalent(new[] { w.Tom.Id, w.Amy.Id }, Told(w, NotificationTopic.ShiftClaimDecided));
        Assert.AreEqual(1, decisions.Count(x => x.Approved));
    }

    [TestMethod]
    public async Task Decide_ManagerCantDecideTheirOwn_AdminCan()
    {
        World w = await SeedAsync();
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = w.Manager.Id, StaffPositionId = w.Csa.Id, IsPrimary = true, CreateDate = Now });
            await ctx.SaveChangesAsync();
        }

        Shift open = await ShiftAsync(w, null);
        ShiftClaim own = (await w.Service.PickUpAsync(w.Manager.Id, open.Id, null)).Data!;

        Assert.IsFalse((await w.Service.DecideAsync(w.ManagerScope, own.Id, true, null, w.Manager.Id)).IsSuccess);
        Assert.IsTrue((await w.Service.DecideAsync(SchedulingTestHelpers.AdminScope, own.Id, true, null, w.Manager.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task Decide_ManagerAtAnotherLocationRefused()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);
        ShiftClaim claim = (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Data!;

        LocationScope elsewhere = new(false, w.Location.Id + 100, w.Company.Id, "Elsewhere", IsManager: true);

        Assert.IsFalse((await w.Service.DecideAsync(elsewhere, claim.Id, true, null, "someone")).IsSuccess);
    }

    // ---- Calling off -----------------------------------------------------------------------

    [TestMethod]
    public async Task Drop_NeedsAReason()
    {
        World w = await SeedAsync();
        Shift mine = await ShiftAsync(w, w.Amy.Id);

        Assert.IsFalse((await w.Service.RequestDropAsync(w.Amy.Id, mine.Id, " ")).IsSuccess);
        Assert.IsFalse((await w.Service.RequestDropAsync(w.Tom.Id, mine.Id, "Not mine")).IsSuccess);
    }

    [TestMethod]
    public async Task Drop_Approved_OpensTheShift_AndOffersItToWhoeverCouldWorkIt()
    {
        World w = await SeedAsync();
        Shift mine = await ShiftAsync(w, w.Amy.Id);
        await ShiftAsync(w, w.Priya.Id, startHour: 10); // Priya's busy then

        ShiftClaim drop = (await w.Service.RequestDropAsync(w.Amy.Id, mine.Id, "Family emergency")).Data!;
        CollectionAssert.Contains(Told(w, NotificationTopic.ShiftClaimPending), w.Manager.Id);
        w.Notifications.Jobs.Clear();

        Result<bool> decided = await w.Service.DecideAsync(w.ManagerScope, drop.Id, true, null, w.Manager.Id);

        Assert.IsTrue(decided.IsSuccess, decided.Error);
        Shift after = await ReloadAsync(w, mine.Id);
        Assert.IsNull(after.UserId);
        Assert.IsTrue(after.IsActive);
        CollectionAssert.AreEqual(new[] { w.Amy.Id }, Told(w, NotificationTopic.ShiftClaimDecided));
        CollectionAssert.AreEquivalent(new[] { w.Tom.Id }, Told(w, NotificationTopic.OpenShift));
    }

    [TestMethod]
    public async Task Drop_TurnedDown_KeepsThemOn()
    {
        World w = await SeedAsync();
        Shift mine = await ShiftAsync(w, w.Amy.Id);
        ShiftClaim drop = (await w.Service.RequestDropAsync(w.Amy.Id, mine.Id, "Tired")).Data!;

        await w.Service.DecideAsync(w.ManagerScope, drop.Id, false, "We're short that day", w.Manager.Id);

        Assert.AreEqual(w.Amy.Id, (await ReloadAsync(w, mine.Id)).UserId);
        ShiftClaimDecidedPayload payload = (ShiftClaimDecidedPayload)w.Notifications.Jobs.Single(x => x.Topic == NotificationTopic.ShiftClaimDecided).Payload;
        Assert.IsFalse(payload.Approved);
        Assert.AreEqual("We're short that day", payload.Reason);
    }

    [TestMethod]
    public async Task Release_OpensAPublishedShift_TellsThePersonAndCover()
    {
        World w = await SeedAsync();
        Shift shift = await ShiftAsync(w, w.Amy.Id);

        Result<bool> released = await w.Service.ReleaseShiftAsync(w.ManagerScope, shift.Id, w.Manager.Id);

        Assert.IsTrue(released.IsSuccess, released.Error);
        Assert.IsNull((await ReloadAsync(w, shift.Id)).UserId);
        CollectionAssert.AreEqual(new[] { w.Amy.Id }, Told(w, NotificationTopic.RotaChanged));
        CollectionAssert.AreEquivalent(new[] { w.Tom.Id, w.Priya.Id }, Told(w, NotificationTopic.OpenShift));
    }

    // ---- Swaps -----------------------------------------------------------------------------

    [TestMethod]
    public async Task Swap_WithApproval_RequestOfferAcceptThenManagerApproves()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        Shift priyas = await ShiftAsync(w, w.Priya.Id, dayOffset: 2);

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, "Wedding")).Data!;
        CollectionAssert.AreEquivalent(new[] { w.Tom.Id, w.Priya.Id }, Told(w, NotificationTopic.SwapRequest));
        w.Notifications.Jobs.Clear();

        ShiftClaim tomOffer = (await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id)).Data!;
        ShiftClaim priyaOffer = (await w.Service.OfferSwapAsync(w.Priya.Id, swap.Id, priyas.Id)).Data!;
        CollectionAssert.AreEqual(new[] { w.Amy.Id, w.Amy.Id }, Told(w, NotificationTopic.SwapRequest));

        Result<ShiftClaim> accepted = await w.Service.AcceptOfferAsync(w.Amy.Id, tomOffer.Id);
        Assert.IsTrue(accepted.IsSuccess, accepted.Error);
        Assert.AreEqual(ShiftClaimStatus.Accepted, (await ClaimAsync(w, swap.Id)).Status);
        Assert.AreEqual(w.Amy.Id, (await ReloadAsync(w, amys.Id)).UserId);
        CollectionAssert.Contains(Told(w, NotificationTopic.ShiftClaimPending), w.Manager.Id);
        w.Notifications.Jobs.Clear();

        Result<bool> approved = await w.Service.DecideAsync(w.ManagerScope, swap.Id, true, null, w.Manager.Id);

        Assert.IsTrue(approved.IsSuccess, approved.Error);
        Assert.AreEqual(w.Tom.Id, (await ReloadAsync(w, amys.Id)).UserId);
        Assert.AreEqual(w.Amy.Id, (await ReloadAsync(w, toms.Id)).UserId);
        Assert.AreEqual(w.Priya.Id, (await ReloadAsync(w, priyas.Id)).UserId);
        Assert.AreEqual(ShiftClaimStatus.Approved, (await ClaimAsync(w, swap.Id)).Status);
        Assert.AreEqual(ShiftClaimStatus.Approved, (await ClaimAsync(w, tomOffer.Id)).Status);
        Assert.AreEqual(ShiftClaimStatus.Rejected, (await ClaimAsync(w, priyaOffer.Id)).Status);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id, w.Tom.Id, w.Priya.Id }, Told(w, NotificationTopic.ShiftClaimDecided));
    }

    [TestMethod]
    public async Task Swap_WithoutApproval_HappensWhenAccepted()
    {
        World w = await SeedAsync(claimsNeedApproval: false);
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;
        ShiftClaim offer = (await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id)).Data!;
        await w.Service.AcceptOfferAsync(w.Amy.Id, offer.Id);

        Assert.AreEqual(w.Tom.Id, (await ReloadAsync(w, amys.Id)).UserId);
        Assert.AreEqual(w.Amy.Id, (await ReloadAsync(w, toms.Id)).UserId);
        Assert.AreEqual(ShiftClaimStatus.Approved, (await ClaimAsync(w, swap.Id)).Status);
    }

    [TestMethod]
    public async Task Swap_SameTimeShiftsCanBeSwapped()
    {
        World w = await SeedAsync(claimsNeedApproval: false);
        Shift amys = await ShiftAsync(w, w.Amy.Id, startHour: 9);
        Shift toms = await ShiftAsync(w, w.Tom.Id, startHour: 10);

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;
        Result<ShiftClaim> offer = await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id);

        // Each gives up the shift that would otherwise clash.
        Assert.IsTrue(offer.IsSuccess, offer.Error);
    }

    [TestMethod]
    public async Task Swap_OfferRefused_WhenNotYoursOrTheyCantWorkIt()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        await ShiftAsync(w, w.Amy.Id, startHour: 10, dayOffset: 1); // Amy's already on the day of Tom's shift

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;

        Assert.IsFalse((await w.Service.OfferSwapAsync(w.Priya.Id, swap.Id, toms.Id)).IsSuccess, "Priya offering Tom's shift");
        StringAssert.Contains((await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id)).Error, "They can't take that shift");
        Assert.IsFalse((await w.Service.OfferSwapAsync(w.Amy.Id, swap.Id, amys.Id)).IsSuccess, "Offering on your own request");
    }

    [TestMethod]
    public async Task Withdraw_AcceptedOffer_PutsTheSwapBackToCollectingOffers()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;
        ShiftClaim offer = (await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id)).Data!;
        await w.Service.AcceptOfferAsync(w.Amy.Id, offer.Id);

        Result<bool> withdrawn = await w.Service.WithdrawAsync(w.Tom.Id, offer.Id);

        Assert.IsTrue(withdrawn.IsSuccess, withdrawn.Error);
        Assert.AreEqual(ShiftClaimStatus.Pending, (await ClaimAsync(w, swap.Id)).Status);
        Assert.IsFalse((await w.Service.WithdrawAsync(w.Amy.Id, offer.Id)).IsSuccess, "Can't withdraw someone else's offer");
    }

    [TestMethod]
    public async Task Swap_TurnedDown_ClosesTheOtherOffersToo()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        Shift priyas = await ShiftAsync(w, w.Priya.Id, dayOffset: 2);

        ShiftClaim swap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;
        ShiftClaim tomOffer = (await w.Service.OfferSwapAsync(w.Tom.Id, swap.Id, toms.Id)).Data!;
        ShiftClaim priyaOffer = (await w.Service.OfferSwapAsync(w.Priya.Id, swap.Id, priyas.Id)).Data!;
        await w.Service.AcceptOfferAsync(w.Amy.Id, tomOffer.Id);
        w.Notifications.Jobs.Clear();

        await w.Service.DecideAsync(w.ManagerScope, swap.Id, false, "Short that day", w.Manager.Id);

        Assert.AreEqual(ShiftClaimStatus.Rejected, (await ClaimAsync(w, priyaOffer.Id)).Status);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id, w.Tom.Id, w.Priya.Id }, Told(w, NotificationTopic.ShiftClaimDecided));
    }

    [TestMethod]
    public async Task Review_FlagsASwapWhoseOtherShiftHasStarted()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);

        Shift started;
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            started = new Shift
            {
                Id = Guid.NewGuid(), LocationId = w.Location.Id, UserId = w.Tom.Id, StaffPositionId = w.Csa.Id,
                StartUtc = Now.AddHours(-2), EndUtc = Now.AddHours(2), IsActive = true,
                CreateDate = Now.AddDays(-5), CreateByUserId = w.Manager.Id, PublishedDateUtc = Now.AddDays(-4), PublishedStartUtc = Now.AddHours(-2)
            };
            ctx.Shifts.Add(started);

            ShiftClaim swap = new() { Id = Guid.NewGuid(), ShiftId = amys.Id, UserId = w.Amy.Id, Kind = ShiftClaimKind.Swap, Status = ShiftClaimStatus.Accepted, RequestedUtc = Now };
            ctx.ShiftClaims.Add(swap);
            ctx.ShiftClaims.Add(new ShiftClaim
            {
                Id = Guid.NewGuid(), ShiftId = amys.Id, UserId = w.Tom.Id, Kind = ShiftClaimKind.SwapOffer, Status = ShiftClaimStatus.Accepted,
                ParentClaimId = swap.Id, OfferedShiftId = started.Id, RequestedUtc = Now
            });
            await ctx.SaveChangesAsync();
        }

        ShiftClaimReview review = (await w.Service.GetReviewAsync(w.ManagerScope, w.Location.Id, w.Manager.Id)).Data!;

        StringAssert.Contains(review.Swaps.Single().Problem, "already started");
    }

    // ---- Shifts changing underneath requests -----------------------------------------------

    [TestMethod]
    public async Task EditingAShift_WithdrawsRequestsOnIt()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        ShiftClaim drop = (await w.Service.RequestDropAsync(w.Amy.Id, amys.Id, "Can't")).Data!;

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), w.Notifications, NullLogger<RotaService>.Instance);

        Shift edited = await ReloadAsync(w, amys.Id);
        edited.EndUtc = edited.EndUtc.AddHours(-1);
        Result<ShiftSaveResult> saved = await rota.SaveShiftAsync(w.ManagerScope, edited, w.Manager.Id);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(ShiftClaimStatus.Withdrawn, (await ClaimAsync(w, drop.Id)).Status);
    }

    [TestMethod]
    public async Task EditingAShift_TellsThePersonTheirRequestWasWithdrawn()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        await w.Service.RequestDropAsync(w.Amy.Id, amys.Id, "Can't");
        w.Notifications.Jobs.Clear();

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), w.Notifications, NullLogger<RotaService>.Instance);

        Shift edited = await ReloadAsync(w, amys.Id);
        edited.EndUtc = edited.EndUtc.AddHours(-1);
        await rota.SaveShiftAsync(w.ManagerScope, edited, w.Manager.Id);

        ShiftClaimDecidedPayload told = (ShiftClaimDecidedPayload)w.Notifications.Jobs.Single(x => x.Topic == NotificationTopic.ShiftClaimDecided && x.UserId == w.Amy.Id).Payload;
        Assert.IsFalse(told.Approved);
        Assert.AreEqual(ShiftClaimKind.Drop, told.Kind);
        StringAssert.Contains(told.Reason, "changed the shift");
    }

    [TestMethod]
    public async Task Publish_AnnouncesNewOpenShifts_ToWhoeverCouldWorkThem()
    {
        World w = await SeedAsync();
        await ShiftAsync(w, null, published: false);
        await ShiftAsync(w, w.Priya.Id, startHour: 12); // Priya's busy then

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), w.Notifications, NullLogger<RotaService>.Instance);

        DateOnly day = RotaTime.LocalDate(Day.AddHours(9));
        Result<PublishResult> published = await rota.PublishRangeAsync(w.ManagerScope, w.Location.Id, day, day, w.Manager.Id);

        Assert.IsTrue(published.IsSuccess, published.Error);
        Assert.AreEqual(1, published.Data!.OpenedShifts.Count);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id, w.Tom.Id }, Told(w, NotificationTopic.OpenShift));
    }

    [TestMethod]
    public async Task RotaView_ListsOpenShiftsSeparately()
    {
        World w = await SeedAsync();
        await ShiftAsync(w, null);
        await ShiftAsync(w, w.Amy.Id);

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        RotaService rota = new(factory, new ContractRequirementService(factory), w.Notifications, NullLogger<RotaService>.Instance);

        DateOnly day = RotaTime.LocalDate(Day.AddHours(9));
        RotaRangeView view = (await rota.GetRangeViewAsync(w.ManagerScope, w.Location.Id, day, day)).Data!;

        Assert.AreEqual(1, view.OpenShifts.Count);
        Assert.AreEqual(1, view.Rows.Single(r => r.User.Id == w.Amy.Id).Shifts.Count);
    }

    // ---- What staff and managers see -------------------------------------------------------

    [TestMethod]
    public async Task Marketplace_ShowsWhyYouCantTakeAShift_AndHidesOtherPositions()
    {
        World w = await SeedAsync();
        StaffPosition kitchen = await SchedulingTestHelpers.SeedPositionAsync(w.Options, w.Company, "Kitchen");

        Shift csaOpen = await ShiftAsync(w, null);
        await ShiftAsync(w, null, dayOffset: 1, positionId: kitchen.Id);
        await ShiftAsync(w, w.Amy.Id, startHour: 12);

        ShiftMarketplace market = (await w.Service.GetMarketplaceAsync(w.Amy.Id)).Data!;

        OpenShiftView only = market.OpenShifts.Single();
        Assert.AreEqual(csaOpen.Id, only.Shift.Id);
        StringAssert.Contains(only.NotEligibleReason, "already working");
    }

    [TestMethod]
    public async Task Marketplace_ListsSwapsWithShiftsYouCouldOffer()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null);

        ShiftMarketplace tomsView = (await w.Service.GetMarketplaceAsync(w.Tom.Id)).Data!;
        ShiftMarketplace amysView = (await w.Service.GetMarketplaceAsync(w.Amy.Id)).Data!;

        SwapRequestView swap = tomsView.SwapRequests.Single();
        Assert.AreEqual(toms.Id, swap.ShiftsICanOffer.Single().Id);
        Assert.AreEqual(0, amysView.SwapRequests.Count, "Your own request isn't listed for you to answer");
        Assert.AreEqual(ShiftClaimKind.Swap, amysView.MyClaims.Single().Claim.Kind);
    }

    [TestMethod]
    public async Task NavCount_LeavesOutTheManagersOwnRequests()
    {
        World w = await SeedAsync();
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = w.Manager.Id, StaffPositionId = w.Csa.Id, IsPrimary = true, CreateDate = Now });
            await ctx.SaveChangesAsync();
        }

        Shift open = await ShiftAsync(w, null);
        Shift managers = await ShiftAsync(w, w.Manager.Id, dayOffset: 2);
        await w.Service.PickUpAsync(w.Amy.Id, open.Id, null);
        await w.Service.RequestDropAsync(w.Manager.Id, managers.Id, "Holiday");

        Assert.AreEqual(1, (await w.Service.CountPendingForNavAsync(w.ManagerScope, w.Manager.Id)).Data);
        Assert.AreEqual(0, (await w.Service.CountPendingForNavAsync(SchedulingTestHelpers.StaffScopeFor(w.Location), w.Amy.Id)).Data);
    }

    [TestMethod]
    public async Task DeletingAnAccount_AnonymisesClaimsAndWithdrawsOpenOnes()
    {
        World w = await SeedAsync();
        Shift open = await ShiftAsync(w, null);
        ShiftClaim claim = (await w.Service.PickUpAsync(w.Amy.Id, open.Id, null)).Data!;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, w.Amy.Id, Now);
            await ctx.SaveChangesAsync();
        }

        ShiftClaim after = await ClaimAsync(w, claim.Id);
        Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, after.UserId);
        Assert.AreEqual(ShiftClaimStatus.Withdrawn, after.Status);
    }

    [TestMethod]
    public async Task DeletingAnAccount_ReopensTheSwapTheyHadAgreedTo_AndClosesOffersOnTheirs()
    {
        World w = await SeedAsync();
        Shift amys = await ShiftAsync(w, w.Amy.Id);
        Shift toms = await ShiftAsync(w, w.Tom.Id, dayOffset: 1);
        Shift priyas = await ShiftAsync(w, w.Priya.Id, dayOffset: 2);

        // Amy has agreed Tom's offer (waiting for a manager); Tom's own swap has an offer from Priya.
        ShiftClaim amysSwap = (await w.Service.RequestSwapAsync(w.Amy.Id, amys.Id, null)).Data!;
        ShiftClaim tomOffer = (await w.Service.OfferSwapAsync(w.Tom.Id, amysSwap.Id, toms.Id)).Data!;
        await w.Service.AcceptOfferAsync(w.Amy.Id, tomOffer.Id);

        Shift tomsOther = await ShiftAsync(w, w.Tom.Id, dayOffset: 3);
        ShiftClaim tomsSwap = (await w.Service.RequestSwapAsync(w.Tom.Id, tomsOther.Id, null)).Data!;
        ShiftClaim priyaOffer = (await w.Service.OfferSwapAsync(w.Priya.Id, tomsSwap.Id, priyas.Id)).Data!;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, w.Tom.Id, Now);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(ShiftClaimStatus.Pending, (await ClaimAsync(w, amysSwap.Id)).Status);
        Assert.AreEqual(ShiftClaimStatus.Withdrawn, (await ClaimAsync(w, priyaOffer.Id)).Status);
    }

    [TestMethod]
    public async Task LocationSettings_DefaultToApproval_AndOnlyTheLocationsManagerCanChangeThem()
    {
        World w = await SeedAsync();

        Assert.IsTrue((await w.Service.GetLocationSettingsAsync(w.Location.Id)).Data!.ClaimsNeedApproval);
        Assert.IsFalse((await w.Service.SaveLocationSettingsAsync(SchedulingTestHelpers.StaffScopeFor(w.Location), w.Location.Id, false, w.Amy.Id)).IsSuccess);
        Assert.IsTrue((await w.Service.SaveLocationSettingsAsync(w.ManagerScope, w.Location.Id, false, w.Manager.Id)).IsSuccess);
        Assert.IsFalse((await w.Service.GetLocationSettingsAsync(w.Location.Id)).Data!.ClaimsNeedApproval);
    }
}
