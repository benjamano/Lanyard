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

    // What decides whether someone can work a shift, loaded once for a window so many shifts can be
    // checked in memory (My Shifts' Up for grabs checks every open shift and every swap pairing).
    public sealed record WorkerCalendar(HashSet<int> Locations, HashSet<Guid> Positions, List<(Guid Id, DateTime StartUtc, DateTime EndUtc)> Shifts, HashSet<DateOnly> DaysOff)
    {
        public static readonly WorkerCalendar Empty = new([], [], [], []);
    }

    // Calendars for several people across [fromUtc, toUtc): memberships, positions, active shifts
    // and approved time off - four queries whatever the number of people.
    public static async Task<Dictionary<string, WorkerCalendar>> LoadCalendarsAsync(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, DateTime fromUtc, DateTime toUtc)
    {
        List<string> ids = userIds.Distinct().ToList();

        var memberships = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.UserId))
            .Select(x => new { x.UserId, x.LocationId })
            .ToListAsync();

        var positions = await ctx.UserPositions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.UserId))
            .Select(x => new { x.UserId, x.StaffPositionId })
            .ToListAsync();

        var shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && x.UserId != null && ids.Contains(x.UserId) && x.StartUtc < toUtc && x.EndUtc > fromUtc)
            .Select(x => new { UserId = x.UserId!, x.Id, x.StartUtc, x.EndUtc })
            .ToListAsync();

        DateOnly fromDay = RotaTime.LocalDate(fromUtc);
        DateOnly toDay = RotaTime.LocalDate(toUtc);

        List<TimeOffRequest> timeOff = await TimeOffService.LiveTimeOffQuery(ctx, ids, fromDay, toDay)
            .Where(x => x.Status == TimeOffStatus.Approved)
            .ToListAsync();

        return ids.ToDictionary(id => id, id => new WorkerCalendar(
            memberships.Where(x => x.UserId == id).Select(x => x.LocationId).ToHashSet(),
            positions.Where(x => x.UserId == id).Select(x => x.StaffPositionId).ToHashSet(),
            shifts.Where(x => x.UserId == id).Select(x => (x.Id, x.StartUtc, x.EndUtc)).ToList(),
            timeOff.Where(x => x.UserId == id)
                .SelectMany(x => Enumerable.Range(0, x.EndDate.DayNumber - x.StartDate.DayNumber + 1).Select(d => x.StartDate.AddDays(d)))
                .ToHashSet()));
    }

    // Why this person can't work this shift, or null if they can. The same rules as the rota
    // builder: at the location, holding the shift's position (if it has one), not already working
    // then, and not on approved time off that day. givingUpShiftId is a shift they hand over in
    // the same move (a swap), which doesn't count as a clash. The calendar must cover the shift.
    public static string? WhyCantWork(WorkerCalendar calendar, Shift shift, Guid? givingUpShiftId, bool aboutViewer)
    {
        string you = aboutViewer ? "You" : "They";
        string youre = aboutViewer ? "You're" : "They're";

        if (!calendar.Locations.Contains(shift.LocationId))
        {
            return $"{youre} not at this location.";
        }

        if (shift.StaffPositionId is Guid positionId && !calendar.Positions.Contains(positionId))
        {
            return $"{you} don't work as {shift.StaffPosition?.Name ?? "this position"}.";
        }

        bool clash = calendar.Shifts.Any(x => x.Id != shift.Id && x.Id != givingUpShiftId
            && x.StartUtc < shift.EndUtc && x.EndUtc > shift.StartUtc);

        if (clash)
        {
            return $"{youre} already working then.";
        }

        return calendar.DaysOff.Contains(RotaTime.LocalDate(shift.StartUtc)) ? $"{youre} booked off that day." : null;
    }

    // One person, one shift: loads just the calendar around that shift.
    public static async Task<string?> WhyCantWorkAsync(ApplicationDbContext ctx, string userId, Shift shift, Guid? givingUpShiftId, bool aboutViewer)
    {
        Dictionary<string, WorkerCalendar> calendars = await LoadCalendarsAsync(ctx, [userId], shift.StartUtc.AddDays(-1), shift.EndUtc.AddDays(1));

        return WhyCantWork(calendars.GetValueOrDefault(userId, WorkerCalendar.Empty), shift, givingUpShiftId, aboutViewer);
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
            .TagWithCallSite()
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
            .TagWithCallSite()
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
            .Open()
            .Where(x => !except.Contains(x.Id)
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

    // Tells each person whose request was withdrawn (by WithdrawForShiftsAsync) that it's gone and
    // why - otherwise someone who said "I can't make it" would believe their manager knew. Call
    // after saving.
    public static async Task NotifyWithdrawnAsync(ApplicationDbContext ctx, INotificationDispatcher notifications, IReadOnlyCollection<ShiftClaim> withdrawn)
    {
        if (withdrawn.Count == 0)
        {
            return;
        }

        List<Guid> shiftIds = withdrawn.Select(x => x.ShiftId).Distinct().ToList();

        Dictionary<Guid, Shift> shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Location)
            .Include(x => x.StaffPosition)
            .Where(x => shiftIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (ShiftClaim claim in withdrawn)
        {
            if (!shifts.TryGetValue(claim.ShiftId, out Shift? shift))
            {
                continue;
            }

            notifications.Enqueue([claim.UserId], NotificationTopic.ShiftClaimDecided, new ShiftClaimDecidedPayload(
                shift.LocationId, shift.Location?.Name ?? "your location", claim.Kind, false, RotaService.ToEmailLine(shift), null, claim.DecisionReason));
        }
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
