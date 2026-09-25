using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// The rules open shifts, call-offs and swaps share, kept here so RotaService (publishing and
// editing shifts) and ShiftClaimService (the requests themselves) can't disagree about them.
internal static class ShiftClaimRules
{
    // A shift staff can act on: published, still on the rota, and exactly as published - one with
    // unpublished edits holds times nobody has been told about yet.
    public static IQueryable<Shift> Settled(IQueryable<Shift> shifts) =>
        shifts.Where(x => x.IsActive && x.PublishedDateUtc != null && (x.UpdateDate == null || x.UpdateDate <= x.PublishedDateUtc));

    public static bool IsSettled(Shift shift) =>
        shift.IsActive && shift.PublishedDateUtc != null && (shift.UpdateDate == null || shift.UpdateDate <= shift.PublishedDateUtc);

    public static async Task<bool> ClaimsNeedApprovalAsync(ApplicationDbContext ctx, int locationId)
    {
        LocationSchedulingSettings? settings = await ctx.LocationSchedulingSettings
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.LocationId == locationId);

        return settings?.ClaimsNeedApproval ?? true;
    }

    // Why this person can't work this shift, or null if they can. The same rules as the rota
    // builder: at the location, holding the shift's position (if it has one), not already working
    // then, and not on approved time off that day. givingUpShiftId is a shift they hand over in
    // the same move (a swap), which doesn't count as a clash.
    public static async Task<string?> WhyCantWorkAsync(ApplicationDbContext ctx, string userId, Shift shift, Guid? givingUpShiftId, bool aboutViewer)
    {
        string you = aboutViewer ? "You" : "They";
        string youre = aboutViewer ? "You're" : "They're";

        bool isMember = await ctx.UserLocationMemberships
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.LocationId == shift.LocationId);

        if (!isMember)
        {
            return $"{youre} not at this location.";
        }

        if (shift.StaffPositionId is Guid positionId)
        {
            bool holds = await ctx.UserPositions.AsNoTracking().AnyAsync(x => x.UserId == userId && x.StaffPositionId == positionId);

            if (!holds)
            {
                return $"{you} don't work as {shift.StaffPosition?.Name ?? "this position"}.";
            }
        }

        Guid ignore = givingUpShiftId ?? Guid.Empty;

        bool clash = await ctx.Shifts
            .AsNoTracking()
            .AnyAsync(x => x.IsActive && x.UserId == userId && x.Id != shift.Id && x.Id != ignore
                && x.StartUtc < shift.EndUtc && x.EndUtc > shift.StartUtc);

        if (clash)
        {
            return $"{youre} already working then.";
        }

        DateOnly day = RotaTime.LocalDate(shift.StartUtc);
        bool off = await TimeOffService.LiveTimeOffQuery(ctx, [userId], day, day).AnyAsync(x => x.Status == TimeOffStatus.Approved);

        return off ? $"{youre} booked off that day." : null;
    }

    // Everyone who could pick up this open shift right now, for the announcement.
    public static async Task<List<string>> EligibleForAsync(ApplicationDbContext ctx, Shift shift, IReadOnlyCollection<string> except)
    {
        List<string> candidates = await PositionHoldersAtAsync(ctx, shift);
        candidates = candidates.Where(x => !except.Contains(x)).ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        HashSet<string> busy = (await ctx.Shifts
            .AsNoTracking()
            .Where(x => x.IsActive && x.UserId != null && candidates.Contains(x.UserId) && x.Id != shift.Id
                && x.StartUtc < shift.EndUtc && x.EndUtc > shift.StartUtc)
            .Select(x => x.UserId!)
            .ToListAsync()).ToHashSet();

        DateOnly day = RotaTime.LocalDate(shift.StartUtc);

        HashSet<string> off = (await TimeOffService.LiveTimeOffQuery(ctx, candidates, day, day)
            .Where(x => x.Status == TimeOffStatus.Approved)
            .Select(x => x.UserId)
            .ToListAsync()).ToHashSet();

        return candidates.Where(x => !busy.Contains(x) && !off.Contains(x)).ToList();
    }

    // Colleagues who could take this shift in a swap: at the location and holding its position.
    // Clashes aren't checked - they'd be handing back one of their own shifts, often one at the
    // very same time - that's checked when they offer.
    public static async Task<List<string>> SwapAudienceAsync(ApplicationDbContext ctx, Shift shift, string requesterId)
    {
        return (await PositionHoldersAtAsync(ctx, shift)).Where(x => x != requesterId).ToList();
    }

    private static async Task<List<string>> PositionHoldersAtAsync(ApplicationDbContext ctx, Shift shift)
    {
        IQueryable<string> members = ctx.UserLocationMemberships
            .AsNoTracking()
            .Where(x => x.LocationId == shift.LocationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .Select(x => x.UserId);

        if (shift.StaffPositionId is Guid positionId)
        {
            members = members.Where(userId => ctx.UserPositions.Any(p => p.UserId == userId && p.StaffPositionId == positionId));
        }

        return await members.Distinct().ToListAsync();
    }

    // Puts a shift in someone's name (or nobody's, for an open shift) without making it an
    // unpublished change: the request and its decision are how the people involved were told, so
    // the shift counts as published as it now stands. The reminder re-arms for the new person.
    public static void Assign(Shift shift, string? userId, string actingUserId, DateTime nowUtc)
    {
        bool wasPublished = shift.PublishedDateUtc is not null;

        shift.UserId = userId;
        shift.UpdateDate = nowUtc;
        shift.UpdateByUserId = actingUserId;
        shift.ReminderSentForStartUtc = null;

        if (wasPublished)
        {
            shift.PublishedDateUtc = nowUtc;
            shift.PublishedByUserId = actingUserId;
            shift.PublishedStartUtc = shift.StartUtc;
        }
    }

    // Closes every open request involving these shifts - they've been edited, removed or handed
    // to someone, so what was asked no longer holds. An accepted swap offer that goes this way
    // puts its swap back to collecting offers. Changes are left for the caller to save.
    // exceptClaimIds are claims the caller has just settled itself.
    public static async Task<List<ShiftClaim>> WithdrawForShiftsAsync(ApplicationDbContext ctx, IReadOnlyCollection<Guid> shiftIds, string reason, DateTime nowUtc, IReadOnlyCollection<Guid>? exceptClaimIds = null)
    {
        if (shiftIds.Count == 0)
        {
            return [];
        }

        List<Guid> except = exceptClaimIds?.ToList() ?? [];

        List<ShiftClaim> open = await ctx.ShiftClaims
            .Where(x => (x.Status == ShiftClaimStatus.Pending || x.Status == ShiftClaimStatus.Accepted) && !except.Contains(x.Id)
                && (shiftIds.Contains(x.ShiftId) || (x.OfferedShiftId != null && shiftIds.Contains(x.OfferedShiftId.Value))))
            .ToListAsync();

        foreach (ShiftClaim claim in open)
        {
            bool wasAcceptedOffer = claim.Kind == ShiftClaimKind.SwapOffer && claim.Status == ShiftClaimStatus.Accepted;

            Close(claim, ShiftClaimStatus.Withdrawn, reason, null, nowUtc);

            if (wasAcceptedOffer && claim.ParentClaimId is Guid parentId && open.All(x => x.Id != parentId) && !except.Contains(parentId))
            {
                ShiftClaim? parent = await ctx.ShiftClaims.FirstOrDefaultAsync(x => x.Id == parentId);

                if (parent is { Status: ShiftClaimStatus.Accepted })
                {
                    parent.Status = ShiftClaimStatus.Pending;
                }
            }
        }

        return open;
    }

    public static void Close(ShiftClaim claim, ShiftClaimStatus status, string? reason, string? decidedByUserId, DateTime nowUtc)
    {
        claim.Status = status;
        claim.DecisionReason = reason;
        claim.DecidedByUserId = decidedByUserId;
        claim.DecidedUtc = nowUtc;
    }

    // Tells everyone who could work an open shift that it's there. The shift needs Location and
    // StaffPosition loaded.
    public static async Task AnnounceOpenShiftAsync(ApplicationDbContext ctx, INotificationDispatcher notifications, Shift shift, IReadOnlyCollection<string> except)
    {
        List<string> eligible = await EligibleForAsync(ctx, shift, except);

        if (eligible.Count == 0)
        {
            return;
        }

        bool needsApproval = await ClaimsNeedApprovalAsync(ctx, shift.LocationId);

        notifications.Enqueue(eligible, NotificationTopic.OpenShift,
            new OpenShiftPayload(shift.LocationId, shift.Location?.Name ?? "your location", RotaService.ToEmailLine(shift), needsApproval));
    }
}
