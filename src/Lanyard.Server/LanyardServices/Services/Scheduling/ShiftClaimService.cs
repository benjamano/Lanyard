using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class ShiftClaimService(
    IDbContextFactory<ApplicationDbContext> factory,
    INotificationDispatcher notifications,
    IShiftClaimEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<ShiftClaimService> logger) : IShiftClaimService
{
    // How far ahead My Shifts looks for open shifts and swaps.
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(62);

    private const int MaxNoteLength = 500;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly IShiftClaimEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ShiftClaimService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    // ---- Staff -----------------------------------------------------------------------------

    public async Task<Result<ShiftMarketplace>> GetMarketplaceAsync(string userId)
    {
        try
        {
            DateTime now = Now;
            DateTime horizon = now + Horizon;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<int> myLocations = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId)
                .Select(x => x.LocationId)
                .ToListAsync();

            List<ShiftClaim> myOpenClaims = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Shift).ThenInclude(x => x!.Location)
                .Include(x => x.Shift).ThenInclude(x => x!.StaffPosition)
                .Include(x => x.OfferedShift).ThenInclude(x => x!.StaffPosition)
                .Open()
                .Where(x => x.UserId == userId && x.Shift!.StartUtc > now)
                .ToListAsync();

            Dictionary<int, bool> approvalByLocation = await ApprovalByLocationAsync(ctx, myLocations);

            // Everything eligibility depends on, loaded once for the whole window and checked in
            // memory: a query per shift (or per swap pairing) adds up fast on this page.
            DateTime windowStart = now.AddDays(-1);
            DateTime windowEnd = horizon.AddDays(1);

            ShiftClaimRules.WorkerCalendar me = (await ShiftClaimRules.LoadCalendarsAsync(ctx, [userId], windowStart, windowEnd))
                .GetValueOrDefault(userId, ShiftClaimRules.WorkerCalendar.Empty);
            HashSet<Guid> myPositions = me.Positions;

            // Open shifts at the person's locations.
            List<Shift> openShifts = await ShiftClaimRules.Settled(ctx.Shifts)
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.StaffPosition)
                .Where(x => x.UserId == null && myLocations.Contains(x.LocationId) && x.StartUtc > now && x.StartUtc < horizon)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            List<OpenShiftView> openViews = [];

            foreach (Shift shift in openShifts)
            {
                // Someone who could never work it (another position) doesn't need to see it at all.
                if (shift.StaffPositionId is Guid positionId && !myPositions.Contains(positionId))
                {
                    continue;
                }

                ShiftClaim? mine = myOpenClaims.FirstOrDefault(x => x.Kind == ShiftClaimKind.Pickup && x.ShiftId == shift.Id);
                string? reason = mine is null ? ShiftClaimRules.WhyCantWork(me, shift, null, aboutViewer: true) : null;

                openViews.Add(new OpenShiftView(shift, approvalByLocation.GetValueOrDefault(shift.LocationId, true), mine, reason));
            }

            // Colleagues' swap requests the person could answer.
            List<ShiftClaim> swaps = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.Shift).ThenInclude(x => x!.Location)
                .Include(x => x.Shift).ThenInclude(x => x!.StaffPosition)
                .Where(x => x.Kind == ShiftClaimKind.Swap && x.Status == ShiftClaimStatus.Pending && x.UserId != userId
                    && x.Shift!.IsActive && x.Shift.UserId == x.UserId
                    && myLocations.Contains(x.Shift.LocationId) && x.Shift.StartUtc > now)
                .OrderBy(x => x.Shift!.StartUtc)
                .ToListAsync();

            List<Shift> myShifts = await ShiftClaimRules.Settled(ctx.Shifts)
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => x.UserId == userId && x.StartUtc > now && x.StartUtc < horizon)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            Dictionary<string, ShiftClaimRules.WorkerCalendar> requesters =
                await ShiftClaimRules.LoadCalendarsAsync(ctx, swaps.Select(x => x.UserId).ToList(), windowStart, windowEnd);

            List<SwapRequestView> swapViews = [];

            foreach (ShiftClaim swap in swaps)
            {
                Shift theirShift = swap.Shift!;

                if (!ShiftClaimRules.IsSettled(theirShift))
                {
                    continue;
                }

                if (theirShift.StaffPositionId is Guid positionId && !myPositions.Contains(positionId))
                {
                    continue;
                }

                ShiftClaim? myOffer = myOpenClaims.FirstOrDefault(x => x.Kind == ShiftClaimKind.SwapOffer && x.ParentClaimId == swap.Id);
                List<Shift> canOffer = [];

                if (myOffer is null)
                {
                    ShiftClaimRules.WorkerCalendar requester = requesters.GetValueOrDefault(swap.UserId, ShiftClaimRules.WorkerCalendar.Empty);

                    foreach (Shift mine in myShifts.Where(x => x.LocationId == theirShift.LocationId))
                    {
                        if (ShiftClaimRules.WhyCantWork(me, theirShift, mine.Id, aboutViewer: true) is null
                            && ShiftClaimRules.WhyCantWork(requester, mine, theirShift.Id, aboutViewer: false) is null)
                        {
                            canOffer.Add(mine);
                        }
                    }
                }

                swapViews.Add(new SwapRequestView(swap, theirShift, RotaNames.For(swap.User), myOffer, canOffer));
            }

            // The person's own open requests, with the offers on their swaps.
            List<Guid> mySwapIds = myOpenClaims.Where(x => x.Kind == ShiftClaimKind.Swap).Select(x => x.Id).ToList();

            List<ShiftClaim> offersOnMine = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.OfferedShift).ThenInclude(x => x!.StaffPosition)
                .Open()
                .Where(x => x.ParentClaimId != null && mySwapIds.Contains(x.ParentClaimId.Value))
                .ToListAsync();

            List<MyClaimView> myViews = myOpenClaims
                .OrderBy(x => x.Shift!.StartUtc)
                .Select(claim => new MyClaimView(
                    claim,
                    claim.Shift!,
                    claim.OfferedShift,
                    offersOnMine.Where(o => o.ParentClaimId == claim.Id)
                        .OrderBy(o => o.OfferedShift!.StartUtc)
                        .Select(o => new SwapOfferView(o, o.OfferedShift!, RotaNames.For(o.User)))
                        .ToList()))
                .ToList();

            return Result<ShiftMarketplace>.Ok(new ShiftMarketplace(openViews, swapViews, myViews) { LocationIds = myLocations });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load open shifts and swaps for {UserId}", userId);
            return Result<ShiftMarketplace>.Fail($"Failed to load open shifts and swaps: {ex.Message}");
        }
    }

    public async Task<Result<List<ShiftClaim>>> GetOpenClaimsForUserAsync(string userId)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ShiftClaim> claims = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Open()
                .Where(x => x.UserId == userId && x.Shift!.StartUtc > now)
                .ToListAsync();

            return Result<List<ShiftClaim>>.Ok(claims);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load open shift requests for {UserId}", userId);
            return Result<List<ShiftClaim>>.Fail($"Failed to load your requests: {ex.Message}");
        }
    }

    public async Task<Result<ShiftClaim>> PickUpAsync(string userId, Guid shiftId, string? note)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Shift? shift = await LoadShiftAsync(ctx, shiftId, tracked: false);

            if (shift is null || !ShiftClaimRules.IsSettled(shift) || shift.StartUtc <= now)
            {
                return Result<ShiftClaim>.Fail("That shift isn't available any more.");
            }

            if (shift.UserId is not null)
            {
                return Result<ShiftClaim>.Fail("Someone has already picked up that shift.");
            }

            if (await HasOpenClaimAsync(ctx, userId, shiftId, ShiftClaimKind.Pickup))
            {
                return Result<ShiftClaim>.Fail("You've already asked for this shift.");
            }

            string? reason = await ShiftClaimRules.WhyCantWorkAsync(ctx, userId, shift, null, aboutViewer: true);

            if (reason is not null)
            {
                return Result<ShiftClaim>.Fail(reason);
            }

            bool needsApproval = await ShiftClaimRules.ClaimsNeedApprovalAsync(ctx, shift.LocationId);

            ShiftClaim claim = NewClaim(shiftId, userId, ShiftClaimKind.Pickup, note, now);

            if (needsApproval)
            {
                ctx.ShiftClaims.Add(claim);
                await ctx.SaveChangesAsync();

                await NotifySafelyAsync(async () =>
                {
                    string name = await NameOfAsync(ctx, userId);
                    await NotifyManagersAsync(ctx, shift.LocationId, [userId],
                        new ShiftClaimPendingPayload(shift.LocationId, LocationName(shift), ShiftClaimKind.Pickup, name, RotaService.ToEmailLine(shift), null, null, claim.Note));
                }, claim.Id);
            }
            else
            {
                // First come, first served: the shift is only taken if it's still open at the moment
                // of the update, so two people tapping at once can't both get it.
                await using IDbContextTransaction? transaction = ctx.Database.IsRelational() ? await ctx.Database.BeginTransactionAsync() : null;

                if (!await ReassignAsync(ctx, shiftId, null, userId, userId, now))
                {
                    return Result<ShiftClaim>.Fail("Someone has just picked up that shift.");
                }

                ShiftClaimRules.Close(claim, ShiftClaimStatus.Approved, null, null, now);
                ctx.ShiftClaims.Add(claim);
                List<ShiftClaim> beaten = await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [shiftId], "Someone else picked up the shift first.", now);
                await ctx.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                await NotifySafelyAsync(() => ShiftClaimRules.NotifyWithdrawnAsync(ctx, _notifications, beaten), claim.Id);
            }

            _logger.LogInformation("{UserId} {Action} open shift {ShiftId}", userId, needsApproval ? "asked for" : "picked up", shiftId);
            _eventBus.Publish(shift.LocationId);

            return Result<ShiftClaim>.Ok(claim);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pick up shift {ShiftId} for {UserId}", shiftId, userId);
            return Result<ShiftClaim>.Fail($"Failed to pick up the shift: {ex.Message}");
        }
    }

    public async Task<Result<ShiftClaim>> RequestDropAsync(string userId, Guid shiftId, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Result<ShiftClaim>.Fail("Say why you can't make it, so your manager can find cover.");
            }

            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Shift? shift = await LoadShiftAsync(ctx, shiftId, tracked: false);
            string? problem = OwnShiftProblem(shift, userId, now);

            if (problem is not null)
            {
                return Result<ShiftClaim>.Fail(problem);
            }

            if (await HasOpenRequestOnOwnShiftAsync(ctx, userId, shiftId))
            {
                return Result<ShiftClaim>.Fail("You've already asked to change this shift. Withdraw that request first.");
            }

            ShiftClaim claim = NewClaim(shiftId, userId, ShiftClaimKind.Drop, reason, now);
            ctx.ShiftClaims.Add(claim);
            await ctx.SaveChangesAsync();

            await NotifySafelyAsync(async () =>
            {
                string name = await NameOfAsync(ctx, userId);
                await NotifyManagersAsync(ctx, shift!.LocationId, [userId],
                    new ShiftClaimPendingPayload(shift.LocationId, LocationName(shift), ShiftClaimKind.Drop, name, RotaService.ToEmailLine(shift), null, null, claim.Note));
            }, claim.Id);

            _logger.LogInformation("{UserId} asked to call off shift {ShiftId}", userId, shiftId);
            _eventBus.Publish(shift!.LocationId);

            return Result<ShiftClaim>.Ok(claim);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request a call-off of shift {ShiftId} for {UserId}", shiftId, userId);
            return Result<ShiftClaim>.Fail($"Failed to send your request: {ex.Message}");
        }
    }

    public async Task<Result<ShiftClaim>> RequestSwapAsync(string userId, Guid shiftId, string? note)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Shift? shift = await LoadShiftAsync(ctx, shiftId, tracked: false);
            string? problem = OwnShiftProblem(shift, userId, now);

            if (problem is not null)
            {
                return Result<ShiftClaim>.Fail(problem);
            }

            if (await HasOpenRequestOnOwnShiftAsync(ctx, userId, shiftId))
            {
                return Result<ShiftClaim>.Fail("You've already asked to change this shift. Withdraw that request first.");
            }

            ShiftClaim claim = NewClaim(shiftId, userId, ShiftClaimKind.Swap, note, now);
            ctx.ShiftClaims.Add(claim);
            await ctx.SaveChangesAsync();

            await NotifySafelyAsync(async () =>
            {
                List<string> audience = await ShiftClaimRules.SwapAudienceAsync(ctx, shift!, userId);
                string name = await NameOfAsync(ctx, userId);

                _notifications.Enqueue(audience, NotificationTopic.SwapRequest,
                    new SwapRequestedPayload(shift!.LocationId, LocationName(shift), name, RotaService.ToEmailLine(shift), claim.Note));
            }, claim.Id);

            _logger.LogInformation("{UserId} asked for a swap on shift {ShiftId}", userId, shiftId);
            _eventBus.Publish(shift!.LocationId);

            return Result<ShiftClaim>.Ok(claim);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request a swap of shift {ShiftId} for {UserId}", shiftId, userId);
            return Result<ShiftClaim>.Fail($"Failed to ask for a swap: {ex.Message}");
        }
    }

    public async Task<Result<ShiftClaim>> OfferSwapAsync(string userId, Guid swapClaimId, Guid myShiftId)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ShiftClaim? swap = await ctx.ShiftClaims.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == swapClaimId && x.Kind == ShiftClaimKind.Swap);

            if (swap is null || swap.Status != ShiftClaimStatus.Pending)
            {
                return Result<ShiftClaim>.Fail("That swap request isn't open any more.");
            }

            if (swap.UserId == userId)
            {
                return Result<ShiftClaim>.Fail("You can't offer a swap on your own request.");
            }

            Shift? theirShift = await LoadShiftAsync(ctx, swap.ShiftId, tracked: false);
            Shift? myShift = await LoadShiftAsync(ctx, myShiftId, tracked: false);

            if (theirShift is null || theirShift.UserId != swap.UserId || !ShiftClaimRules.IsSettled(theirShift) || theirShift.StartUtc <= now)
            {
                return Result<ShiftClaim>.Fail("That swap request isn't open any more.");
            }

            string? mineProblem = OwnShiftProblem(myShift, userId, now);

            if (mineProblem is not null)
            {
                return Result<ShiftClaim>.Fail(mineProblem);
            }

            if (myShift!.LocationId != theirShift.LocationId)
            {
                return Result<ShiftClaim>.Fail("You can only swap for a shift at the same location.");
            }

            bool alreadyOffered = await ctx.ShiftClaims.Open().AnyAsync(x => x.ParentClaimId == swapClaimId && x.UserId == userId);

            if (alreadyOffered)
            {
                return Result<ShiftClaim>.Fail("You've already offered a swap for this shift.");
            }

            string? youCant = await ShiftClaimRules.WhyCantWorkAsync(ctx, userId, theirShift, myShift.Id, aboutViewer: true);

            if (youCant is not null)
            {
                return Result<ShiftClaim>.Fail(youCant);
            }

            string? theyCant = await ShiftClaimRules.WhyCantWorkAsync(ctx, swap.UserId, myShift, theirShift.Id, aboutViewer: false);

            if (theyCant is not null)
            {
                return Result<ShiftClaim>.Fail($"They can't take that shift: {theyCant}");
            }

            ShiftClaim offer = NewClaim(theirShift.Id, userId, ShiftClaimKind.SwapOffer, null, now);
            offer.ParentClaimId = swapClaimId;
            offer.OfferedShiftId = myShift.Id;

            ctx.ShiftClaims.Add(offer);
            await ctx.SaveChangesAsync();

            await NotifySafelyAsync(async () =>
            {
                string name = await NameOfAsync(ctx, userId);

                _notifications.Enqueue([swap.UserId], NotificationTopic.SwapRequest,
                    new SwapOfferedPayload(theirShift.LocationId, LocationName(theirShift), name, RotaService.ToEmailLine(theirShift), RotaService.ToEmailLine(myShift)));
            }, offer.Id);

            _logger.LogInformation("{UserId} offered shift {OfferedShiftId} for swap {SwapClaimId}", userId, myShiftId, swapClaimId);
            _eventBus.Publish(theirShift.LocationId);

            return Result<ShiftClaim>.Ok(offer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to offer a swap on {SwapClaimId} for {UserId}", swapClaimId, userId);
            return Result<ShiftClaim>.Fail($"Failed to offer the swap: {ex.Message}");
        }
    }

    public async Task<Result<ShiftClaim>> AcceptOfferAsync(string userId, Guid offerClaimId)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ShiftClaim? offer = await ctx.ShiftClaims.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == offerClaimId && x.Kind == ShiftClaimKind.SwapOffer);
            ShiftClaim? swap = offer?.ParentClaimId is Guid parentId ? await ctx.ShiftClaims.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == parentId) : null;

            if (offer is null || swap is null || swap.UserId != userId)
            {
                return Result<ShiftClaim>.Fail("That offer isn't available.");
            }

            if (offer.Status != ShiftClaimStatus.Pending || swap.Status != ShiftClaimStatus.Pending)
            {
                return Result<ShiftClaim>.Fail("That offer isn't open any more.");
            }

            (Shift? mine, Shift? theirs, string? problem) = await LoadSwapShiftsAsync(ctx, swap, offer, now);

            if (problem is not null)
            {
                return Result<ShiftClaim>.Fail(problem);
            }

            bool needsApproval = await ShiftClaimRules.ClaimsNeedApprovalAsync(ctx, mine!.LocationId);

            if (needsApproval)
            {
                swap.Status = ShiftClaimStatus.Accepted;
                offer.Status = ShiftClaimStatus.Accepted;
                await ctx.SaveChangesAsync();

                await NotifySafelyAsync(async () =>
                {
                    string requester = await NameOfAsync(ctx, swap.UserId);
                    string offerer = await NameOfAsync(ctx, offer.UserId);

                    await NotifyManagersAsync(ctx, mine.LocationId, [swap.UserId, offer.UserId],
                        new ShiftClaimPendingPayload(mine.LocationId, LocationName(mine), ShiftClaimKind.Swap, requester,
                            RotaService.ToEmailLine(mine), offerer, RotaService.ToEmailLine(theirs!), swap.Note));
                }, swap.Id);
            }
            else
            {
                await using IDbContextTransaction? transaction = ctx.Database.IsRelational() ? await ctx.Database.BeginTransactionAsync() : null;

                List<ShiftClaim>? otherOffers = await ExecuteSwapAsync(ctx, swap, offer, mine, theirs!, userId, now);

                if (otherOffers is null)
                {
                    return Result<ShiftClaim>.Fail("One of the shifts has just changed. Check your shifts and try again.");
                }

                await ctx.SaveChangesAsync();

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                await NotifySafelyAsync(() => NotifySwapDoneAsync(ctx, swap, offer, mine, theirs!, otherOffers), swap.Id);
            }

            _logger.LogInformation("{UserId} accepted swap offer {OfferId} ({Outcome})", userId, offerClaimId, needsApproval ? "waiting for a manager" : "swapped");
            _eventBus.Publish(mine.LocationId);

            return Result<ShiftClaim>.Ok(offer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept swap offer {OfferId} for {UserId}", offerClaimId, userId);
            return Result<ShiftClaim>.Fail($"Failed to accept the offer: {ex.Message}");
        }
    }

    public async Task<Result<bool>> WithdrawAsync(string userId, Guid claimId)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ShiftClaim? claim = await ctx.ShiftClaims.Include(x => x.Shift).FirstOrDefaultAsync(x => x.Id == claimId);

            if (claim is null || claim.UserId != userId)
            {
                return Result<bool>.Fail("That request isn't yours.");
            }

            if (!claim.IsOpen)
            {
                return Result<bool>.Fail("That request has already been dealt with.");
            }

            ShiftClaimRules.Close(claim, ShiftClaimStatus.Withdrawn, null, userId, now);

            if (claim.Kind == ShiftClaimKind.Swap)
            {
                List<ShiftClaim> offers = await ctx.ShiftClaims
                    .Open()
                    .Where(x => x.ParentClaimId == claimId)
                    .ToListAsync();

                offers.ForEach(x => ShiftClaimRules.Close(x, ShiftClaimStatus.Withdrawn, "They no longer need a swap.", null, now));
            }
            else if (claim.Kind == ShiftClaimKind.SwapOffer && claim.ParentClaimId is Guid parentId)
            {
                ShiftClaim? parent = await ctx.ShiftClaims.FirstOrDefaultAsync(x => x.Id == parentId);

                // Taking back the offer they'd chosen puts their swap back to collecting offers.
                if (parent is { Status: ShiftClaimStatus.Accepted })
                {
                    parent.Status = ShiftClaimStatus.Pending;
                }
            }

            await ctx.SaveChangesAsync();

            _eventBus.Publish(claim.Shift!.LocationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to withdraw claim {ClaimId} for {UserId}", claimId, userId);
            return Result<bool>.Fail($"Failed to withdraw the request: {ex.Message}");
        }
    }

    // ---- Managers --------------------------------------------------------------------------

    public async Task<Result<ShiftClaimReview>> GetReviewAsync(LocationScope scope, int locationId, string viewerUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<ShiftClaimReview>.Fail("You can only review requests for your own location.");
            }

            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ShiftClaim> claims = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.Shift).ThenInclude(x => x!.User)
                .Include(x => x.Shift).ThenInclude(x => x!.StaffPosition)
                .Where(x => x.Shift!.LocationId == locationId && x.Shift.StartUtc > now
                    && ((x.Status == ShiftClaimStatus.Pending && (x.Kind == ShiftClaimKind.Pickup || x.Kind == ShiftClaimKind.Drop))
                        || (x.Status == ShiftClaimStatus.Accepted && x.Kind == ShiftClaimKind.Swap)))
                .OrderBy(x => x.Shift!.StartUtc)
                .ThenBy(x => x.RequestedUtc)
                .ToListAsync();

            List<Guid> acceptedSwapIds = claims.Where(x => x.Kind == ShiftClaimKind.Swap).Select(x => x.Id).ToList();

            List<ShiftClaim> acceptedOffers = await ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.OfferedShift).ThenInclude(x => x!.StaffPosition)
                .Where(x => x.ParentClaimId != null && acceptedSwapIds.Contains(x.ParentClaimId.Value) && x.Status == ShiftClaimStatus.Accepted)
                .ToListAsync();

            List<ClaimReviewItem> pickups = [];
            List<ClaimReviewItem> drops = [];
            List<ClaimReviewItem> swaps = [];

            foreach (ShiftClaim claim in claims)
            {
                Shift shift = claim.Shift!;
                string? problem;

                switch (claim.Kind)
                {
                    case ShiftClaimKind.Pickup:
                        problem = shift.UserId is not null ? "Someone already has this shift."
                            : await ShiftClaimRules.WhyCantWorkAsync(ctx, claim.UserId, shift, null, aboutViewer: false);
                        pickups.Add(new ClaimReviewItem(claim, shift, RotaNames.For(claim.User), null, null, null, OwnRequest(scope, claim.UserId, viewerUserId) ?? problem));
                        break;

                    case ShiftClaimKind.Drop:
                        problem = shift.UserId != claim.UserId ? "They're no longer on this shift." : null;
                        drops.Add(new ClaimReviewItem(claim, shift, RotaNames.For(claim.User), null, null, null, OwnRequest(scope, claim.UserId, viewerUserId) ?? problem));
                        break;

                    default:
                        ShiftClaim? offer = acceptedOffers.FirstOrDefault(x => x.ParentClaimId == claim.Id);

                        if (offer?.OfferedShift is null)
                        {
                            continue;
                        }

                        problem = OwnRequest(scope, claim.UserId, viewerUserId) ?? OwnRequest(scope, offer.UserId, viewerUserId)
                            ?? await SwapProblemAsync(ctx, claim, offer, shift, offer.OfferedShift, now);
                        swaps.Add(new ClaimReviewItem(claim, shift, RotaNames.For(claim.User), offer.OfferedShift, RotaNames.For(offer.User), offer, problem));
                        break;
                }
            }

            List<PickupGroup> groups = pickups
                .GroupBy(x => x.Shift.Id)
                .Select(g => new PickupGroup(g.First().Shift, g.ToList()))
                .ToList();

            bool needsApproval = await ShiftClaimRules.ClaimsNeedApprovalAsync(ctx, locationId);

            return Result<ShiftClaimReview>.Ok(new ShiftClaimReview(groups, drops, swaps, needsApproval));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load shift requests for location {LocationId}", locationId);
            return Result<ShiftClaimReview>.Fail($"Failed to load shift requests: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DecideAsync(LocationScope scope, Guid claimId, bool approve, string? reason, string deciderUserId)
    {
        try
        {
            DateTime now = Now;
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ShiftClaim? claim = await ctx.ShiftClaims.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == claimId);
            Shift? shift = claim is null ? null : await LoadShiftAsync(ctx, claim.ShiftId, tracked: true);

            if (claim is null || shift is null)
            {
                return Result<bool>.Fail("That request no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, shift.LocationId))
            {
                return Result<bool>.Fail("You can only decide requests for your own location.");
            }

            ShiftClaim? offer = claim.Kind == ShiftClaimKind.Swap
                ? await ctx.ShiftClaims.Include(x => x.User).FirstOrDefaultAsync(x => x.ParentClaimId == claim.Id && x.Status == ShiftClaimStatus.Accepted)
                : null;

            if (OwnRequest(scope, claim.UserId, deciderUserId) is not null
                || (offer is not null && OwnRequest(scope, offer.UserId, deciderUserId) is not null))
            {
                return Result<bool>.Fail("You can't decide a request that involves your own shifts. Another manager needs to.");
            }

            bool decidable = claim.Kind switch
            {
                ShiftClaimKind.Pickup or ShiftClaimKind.Drop => claim.Status == ShiftClaimStatus.Pending,
                ShiftClaimKind.Swap => claim.Status == ShiftClaimStatus.Accepted && offer is not null,
                _ => false
            };

            if (!decidable || shift.StartUtc <= now)
            {
                return Result<bool>.Fail("That request has already been dealt with.");
            }

            // Shifts change hands through conditional updates (ReassignAsync); these and the claim
            // changes land together or not at all.
            await using IDbContextTransaction? transaction = ctx.Database.IsRelational() ? await ctx.Database.BeginTransactionAsync() : null;

            Func<Task>? notify;

            switch (claim.Kind, approve)
            {
                case (ShiftClaimKind.Pickup, true):
                {
                    string? problem = shift.UserId is not null ? "Someone already has this shift."
                        : await ShiftClaimRules.WhyCantWorkAsync(ctx, claim.UserId, shift, null, aboutViewer: false);

                    if (problem is not null)
                    {
                        return Result<bool>.Fail(problem);
                    }

                    // Only if it's still open at the moment of the update: two managers approving
                    // different people at once can't both hand it out.
                    if (!await ReassignAsync(ctx, shift.Id, null, claim.UserId, deciderUserId, now))
                    {
                        return Result<bool>.Fail("Someone already has this shift.");
                    }

                    ShiftClaimRules.Close(claim, ShiftClaimStatus.Approved, null, deciderUserId, now);

                    // Everyone else who asked is told it went to someone else.
                    List<ShiftClaim> others = await ctx.ShiftClaims
                        .Where(x => x.ShiftId == shift.Id && x.Id != claim.Id && x.Kind == ShiftClaimKind.Pickup && x.Status == ShiftClaimStatus.Pending)
                        .ToListAsync();

                    others.ForEach(x => ShiftClaimRules.Close(x, ShiftClaimStatus.Rejected, "The shift went to someone else.", deciderUserId, now));

                    notify = () =>
                    {
                        Decided([claim.UserId], shift, claim.Kind, true, null, null);
                        Decided(others.Select(x => x.UserId), shift, claim.Kind, false, null, "The shift went to someone else.");
                        return Task.CompletedTask;
                    };
                    break;
                }

                case (ShiftClaimKind.Drop, true):
                {
                    if (shift.UserId != claim.UserId)
                    {
                        return Result<bool>.Fail("They're no longer on this shift.");
                    }

                    ShiftClaimRules.Assign(shift, null, deciderUserId, now);
                    ShiftClaimRules.Close(claim, ShiftClaimStatus.Approved, reason, deciderUserId, now);
                    await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [shift.Id], "The shift has changed hands.", now, [claim.Id]);

                    notify = async () =>
                    {
                        Decided([claim.UserId], shift, claim.Kind, true, null, reason);
                        await ShiftClaimRules.AnnounceOpenShiftAsync(ctx, _notifications, shift, [claim.UserId]);
                    };
                    break;
                }

                case (ShiftClaimKind.Swap, true):
                {
                    (Shift? mine, Shift? theirs, string? problem) = await LoadSwapShiftsAsync(ctx, claim, offer!, now);

                    if (problem is not null)
                    {
                        return Result<bool>.Fail(problem);
                    }

                    List<ShiftClaim>? otherOffers = await ExecuteSwapAsync(ctx, claim, offer!, mine!, theirs!, deciderUserId, now);

                    if (otherOffers is null)
                    {
                        return Result<bool>.Fail("One of the shifts has changed since the swap was agreed.");
                    }

                    notify = () => NotifySwapDoneAsync(ctx, claim, offer!, mine!, theirs!, otherOffers);
                    break;
                }

                case (ShiftClaimKind.Swap, false):
                {
                    Shift? theirs = await LoadShiftAsync(ctx, offer!.OfferedShiftId!.Value, tracked: false);

                    ShiftClaimRules.Close(claim, ShiftClaimStatus.Rejected, reason, deciderUserId, now);
                    ShiftClaimRules.Close(offer, ShiftClaimStatus.Rejected, reason, deciderUserId, now);

                    // The swap is over, so the offers nobody chose are too.
                    List<ShiftClaim> otherOffers = await ctx.ShiftClaims
                        .Open()
                        .Where(x => x.ParentClaimId == claim.Id && x.Id != offer.Id)
                        .ToListAsync();

                    otherOffers.ForEach(x => ShiftClaimRules.Close(x, ShiftClaimStatus.Rejected, "The swap didn't go ahead.", deciderUserId, now));

                    notify = async () =>
                    {
                        Decided([claim.UserId], shift, ShiftClaimKind.Swap, false, theirs, reason);

                        if (theirs is not null)
                        {
                            Decided([offer.UserId], theirs, ShiftClaimKind.SwapOffer, false, shift, reason);
                        }

                        foreach (ShiftClaim other in otherOffers)
                        {
                            Shift? offered = other.OfferedShiftId is Guid id ? await LoadShiftAsync(ctx, id, tracked: false) : null;

                            if (offered is not null)
                            {
                                Decided([other.UserId], offered, ShiftClaimKind.SwapOffer, false, shift, "The swap didn't go ahead.");
                            }
                        }
                    };
                    break;
                }

                default:
                {
                    ShiftClaimRules.Close(claim, ShiftClaimStatus.Rejected, reason, deciderUserId, now);
                    notify = () =>
                    {
                        Decided([claim.UserId], shift, claim.Kind, false, null, reason);
                        return Task.CompletedTask;
                    };
                    break;
                }
            }

            await ctx.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            await NotifySafelyAsync(notify, claim.Id);

            _logger.LogInformation("{DeciderId} {Decision} {Kind} request {ClaimId}", deciderUserId, approve ? "approved" : "turned down", claim.Kind, claimId);
            _eventBus.Publish(shift.LocationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decide shift request {ClaimId}", claimId);
            return Result<bool>.Fail($"Failed to save the decision: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ReleaseShiftAsync(LocationScope scope, Guid shiftId, string actingUserId)
    {
        try
        {
            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Shift? shift = await LoadShiftAsync(ctx, shiftId, tracked: true);

            if (shift is null || !shift.IsActive)
            {
                return Result<bool>.Fail("That shift no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, shift.LocationId))
            {
                return Result<bool>.Fail("You can only edit the rota for your own location.");
            }

            if (shift.UserId is not string previousUserId)
            {
                return Result<bool>.Fail("That shift is already open.");
            }

            if (!ShiftClaimRules.IsSettled(shift))
            {
                return Result<bool>.Fail("Publish this shift first. To open up a draft, edit it and choose Open shift instead of a person.");
            }

            if (shift.StartUtc <= now)
            {
                return Result<bool>.Fail("That shift has already started.");
            }

            ShiftEmailLine line = RotaService.ToEmailLine(shift);

            ShiftClaimRules.Assign(shift, null, actingUserId, now);
            List<ShiftClaim> withdrawn = await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [shift.Id], "A manager opened the shift up for cover.", now);
            await ctx.SaveChangesAsync();

            await NotifySafelyAsync(async () =>
            {
                await ShiftClaimRules.NotifyWithdrawnAsync(ctx, _notifications, withdrawn);

                _notifications.Enqueue([previousUserId], NotificationTopic.RotaChanged,
                    new RotaChangedPayload(shift.LocationId, LocationName(shift), [], [], [line]));

                await ShiftClaimRules.AnnounceOpenShiftAsync(ctx, _notifications, shift, [previousUserId]);
            }, shift.Id);

            _logger.LogInformation("{UserId} released shift {ShiftId} from {PreviousUserId} for cover", actingUserId, shiftId, previousUserId);
            _eventBus.Publish(shift.LocationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release shift {ShiftId}", shiftId);
            return Result<bool>.Fail($"Failed to open up the shift: {ex.Message}");
        }
    }

    public async Task<Result<int>> CountPendingForNavAsync(LocationScope scope, string? viewerUserId)
    {
        try
        {
            if ((!scope.IsAdmin && !scope.IsManager) || scope.LocationId is not int locationId)
            {
                return Result<int>.Ok(0);
            }

            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<ShiftClaim> waiting = ctx.ShiftClaims
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Shift!.LocationId == locationId && x.Shift.StartUtc > now
                    && ((x.Status == ShiftClaimStatus.Pending && (x.Kind == ShiftClaimKind.Pickup || x.Kind == ShiftClaimKind.Drop))
                        || (x.Status == ShiftClaimStatus.Accepted && x.Kind == ShiftClaimKind.Swap)));

            // A manager's own request is waiting on someone else, not on them.
            if (!scope.IsAdmin && viewerUserId is not null)
            {
                waiting = waiting.Where(x => x.UserId != viewerUserId
                    && !ctx.ShiftClaims.Any(o => o.ParentClaimId == x.Id && o.Status == ShiftClaimStatus.Accepted && o.UserId == viewerUserId));
            }

            return Result<int>.Ok(await waiting.CountAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count shift requests for the nav");
            return Result<int>.Fail($"Failed to count shift requests: {ex.Message}");
        }
    }

    public async Task<Result<LocationSchedulingSettings>> GetLocationSettingsAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            LocationSchedulingSettings settings = await ctx.LocationSchedulingSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.LocationId == locationId)
                ?? new LocationSchedulingSettings { LocationId = locationId };

            return Result<LocationSchedulingSettings>.Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load rota settings for location {LocationId}", locationId);
            return Result<LocationSchedulingSettings>.Fail($"Failed to load the location's rota settings: {ex.Message}");
        }
    }

    public async Task<Result<LocationSchedulingSettings>> SaveLocationSettingsAsync(LocationScope scope, int locationId, bool claimsNeedApproval, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<LocationSchedulingSettings>.Fail("You can only change settings for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            LocationSchedulingSettings? settings = await ctx.LocationSchedulingSettings.FirstOrDefaultAsync(x => x.LocationId == locationId);

            if (settings is null)
            {
                settings = new LocationSchedulingSettings { LocationId = locationId };
                ctx.LocationSchedulingSettings.Add(settings);
            }

            settings.ClaimsNeedApproval = claimsNeedApproval;
            settings.UpdateDate = Now;
            settings.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} set ClaimsNeedApproval={Value} at location {LocationId}", actingUserId, claimsNeedApproval, locationId);
            _eventBus.Publish(locationId);

            return Result<LocationSchedulingSettings>.Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save rota settings for location {LocationId}", locationId);
            return Result<LocationSchedulingSettings>.Fail($"Failed to save the location's rota settings: {ex.Message}");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static ShiftClaim NewClaim(Guid shiftId, string userId, ShiftClaimKind kind, string? note, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        UserId = userId,
        Kind = kind,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, MaxNoteLength)],
        Status = ShiftClaimStatus.Pending,
        RequestedUtc = now
    };

    private static async Task<Shift?> LoadShiftAsync(ApplicationDbContext ctx, Guid shiftId, bool tracked)
    {
        IQueryable<Shift> shifts = tracked ? ctx.Shifts : ctx.Shifts.AsNoTracking();

        return await shifts
            .TagWithCallSite()
            .Include(x => x.Location)
            .Include(x => x.StaffPosition)
            .FirstOrDefaultAsync(x => x.Id == shiftId);
    }

    // Why the person can't ask to change this shift of theirs, or null.
    private static string? OwnShiftProblem(Shift? shift, string userId, DateTime now)
    {
        if (shift is null || !shift.IsActive || shift.UserId != userId)
        {
            return "That isn't one of your shifts.";
        }

        if (!ShiftClaimRules.IsSettled(shift))
        {
            return "This shift has just been changed by a manager. Check the new details, then try again once they're published.";
        }

        return shift.StartUtc <= now ? "That shift has already started." : null;
    }

    private static string? OwnRequest(LocationScope scope, string claimUserId, string viewerUserId) =>
        !scope.IsAdmin && claimUserId == viewerUserId ? "This involves your own shift, so another manager needs to decide it." : null;

    private static Task<bool> HasOpenClaimAsync(ApplicationDbContext ctx, string userId, Guid shiftId, ShiftClaimKind kind) =>
        ctx.ShiftClaims.Open().AnyAsync(x => x.UserId == userId && x.ShiftId == shiftId && x.Kind == kind);

    private static Task<bool> HasOpenRequestOnOwnShiftAsync(ApplicationDbContext ctx, string userId, Guid shiftId) =>
        ctx.ShiftClaims.Open().AnyAsync(x => x.UserId == userId && x.ShiftId == shiftId
            && (x.Kind == ShiftClaimKind.Drop || x.Kind == ShiftClaimKind.Swap));

    // Loads (tracked) the two shifts of a swap and checks the swap can still happen.
    private static async Task<(Shift? Mine, Shift? Theirs, string? Problem)> LoadSwapShiftsAsync(ApplicationDbContext ctx, ShiftClaim swap, ShiftClaim offer, DateTime now)
    {
        Shift? mine = await LoadShiftAsync(ctx, swap.ShiftId, tracked: true);
        Shift? theirs = offer.OfferedShiftId is Guid id ? await LoadShiftAsync(ctx, id, tracked: true) : null;

        if (mine is null || theirs is null)
        {
            return (mine, theirs, "One of the shifts no longer exists.");
        }

        return (mine, theirs, await SwapProblemAsync(ctx, swap, offer, mine, theirs, now));
    }

    private static async Task<string?> SwapProblemAsync(ApplicationDbContext ctx, ShiftClaim swap, ShiftClaim offer, Shift mine, Shift theirs, DateTime? now = null)
    {
        if (mine.UserId != swap.UserId || theirs.UserId != offer.UserId || !ShiftClaimRules.IsSettled(mine) || !ShiftClaimRules.IsSettled(theirs))
        {
            return "One of the shifts has changed since the swap was agreed.";
        }

        if (now is DateTime at && (mine.StartUtc <= at || theirs.StartUtc <= at))
        {
            return "One of the shifts has already started.";
        }

        string? offererCant = await ShiftClaimRules.WhyCantWorkAsync(ctx, offer.UserId, mine, theirs.Id, aboutViewer: false);

        if (offererCant is not null)
        {
            return $"{RotaNames.For(offer.User)} can't take the shift: {offererCant}";
        }

        string? requesterCant = await ShiftClaimRules.WhyCantWorkAsync(ctx, swap.UserId, theirs, mine.Id, aboutViewer: false);

        return requesterCant is null ? null : $"{RotaNames.For(swap.User)} can't take the other shift: {requesterCant}";
    }

    // Swaps the people on the two shifts and settles every claim involved. Returns the offers that
    // weren't chosen, to be told, or null when either shift no longer belongs to who it should (a
    // concurrent swap got there first) - the caller must then not save, and rolls back its
    // transaction. Claim changes are left for the caller to save.
    private static async Task<List<ShiftClaim>?> ExecuteSwapAsync(ApplicationDbContext ctx, ShiftClaim swap, ShiftClaim offer, Shift mine, Shift theirs, string actingUserId, DateTime now)
    {
        if (!await ReassignAsync(ctx, mine.Id, swap.UserId, offer.UserId, actingUserId, now)
            || !await ReassignAsync(ctx, theirs.Id, offer.UserId, swap.UserId, actingUserId, now))
        {
            return null;
        }

        ShiftClaimRules.Close(swap, ShiftClaimStatus.Approved, null, actingUserId, now);
        ShiftClaimRules.Close(offer, ShiftClaimStatus.Approved, null, actingUserId, now);

        List<ShiftClaim> otherOffers = await ctx.ShiftClaims
            .Open()
            .Where(x => x.ParentClaimId == swap.Id && x.Id != offer.Id)
            .ToListAsync();

        otherOffers.ForEach(x => ShiftClaimRules.Close(x, ShiftClaimStatus.Rejected, "They swapped with someone else.", null, now));

        // Anything else still open on either shift (a call-off, another swap) no longer applies.
        await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [mine.Id, theirs.Id], "The shift has changed hands.", now,
            [swap.Id, offer.Id, .. otherOffers.Select(x => x.Id)]);

        return otherOffers;
    }

    private async Task NotifySwapDoneAsync(ApplicationDbContext ctx, ShiftClaim swap, ShiftClaim offer, Shift mine, Shift theirs, List<ShiftClaim> otherOffers)
    {
        // "You now work X instead of Y", from each side.
        Decided([swap.UserId], theirs, ShiftClaimKind.Swap, true, mine, null);
        Decided([offer.UserId], mine, ShiftClaimKind.SwapOffer, true, theirs, null);

        foreach (ShiftClaim other in otherOffers)
        {
            Shift? offered = other.OfferedShiftId is Guid id ? await LoadShiftAsync(ctx, id, tracked: false) : null;

            if (offered is not null)
            {
                Decided([other.UserId], offered, ShiftClaimKind.SwapOffer, false, mine, "They swapped with someone else.");
            }
        }
    }

    private void Decided(IEnumerable<string> userIds, Shift shift, ShiftClaimKind kind, bool approved, Shift? otherShift, string? reason)
    {
        _notifications.Enqueue(userIds, NotificationTopic.ShiftClaimDecided, new ShiftClaimDecidedPayload(
            shift.LocationId,
            LocationName(shift),
            kind,
            approved,
            RotaService.ToEmailLine(shift),
            otherShift is null ? null : RotaService.ToEmailLine(otherShift),
            reason));
    }

    private async Task NotifyManagersAsync(ApplicationDbContext ctx, int locationId, IReadOnlyCollection<string> except, ShiftClaimPendingPayload payload)
    {
        List<string> managers = await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId);

        _notifications.Enqueue(managers.Where(x => !except.Contains(x)), NotificationTopic.ShiftClaimPending, payload);
    }

    private static async Task<string> NameOfAsync(ApplicationDbContext ctx, string userId) =>
        RotaNames.For(await ctx.Users.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == userId));

    private static string LocationName(Shift shift) => shift.Location?.Name ?? "your location";

    private static async Task<Dictionary<int, bool>> ApprovalByLocationAsync(ApplicationDbContext ctx, List<int> locationIds) =>
        await ctx.LocationSchedulingSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => locationIds.Contains(x.LocationId))
            .ToDictionaryAsync(x => x.LocationId, x => x.ClaimsNeedApproval);

    // Moves a published shift from one person (null = open) to another, only if it still belongs to
    // fromUserId at the moment of the update - one conditional UPDATE - so two pick-ups, approvals
    // or swaps racing for the same shift can't both win. False when it had already moved.
    private static async Task<bool> ReassignAsync(ApplicationDbContext ctx, Guid shiftId, string? fromUserId, string? userId, string actingUserId, DateTime now)
    {
        if (ctx.Database.IsRelational())
        {
            int updated = await ctx.Shifts
                .Where(x => x.Id == shiftId && x.UserId == fromUserId && x.IsActive && x.PublishedDateUtc != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.UserId, userId)
                    .SetProperty(x => x.UpdateDate, now)
                    .SetProperty(x => x.UpdateByUserId, actingUserId)
                    .SetProperty(x => x.PublishedDateUtc, now)
                    .SetProperty(x => x.PublishedByUserId, actingUserId)
                    .SetProperty(x => x.PublishedStartUtc, x => x.StartUtc)
                    .SetProperty(x => x.ReminderSentForStartUtc, (DateTime?)null));

            return updated == 1;
        }

        // The EF in-memory provider (tests) has no ExecuteUpdate.
        Shift? shift = await ctx.Shifts.FirstOrDefaultAsync(x => x.Id == shiftId && x.UserId == fromUserId && x.IsActive);

        if (shift is null)
        {
            return false;
        }

        ShiftClaimRules.Assign(shift, userId, actingUserId, now);

        return true;
    }

    // Notifications go out after the change is saved; a failure building one is logged, never
    // reported as the request failing.
    private async Task NotifySafelyAsync(Func<Task> notify, Guid id)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saved shift request change {Id} but couldn't queue its notifications", id);
        }
    }
}
