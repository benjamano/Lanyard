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
    // Requirements is the training each of their positions needs (shared by every calendar loaded
    // together); Training is their record on those courses.
    public sealed record WorkerCalendar(
        HashSet<int> Locations,
        HashSet<Guid> Positions,
        List<(Guid Id, DateTime StartUtc, DateTime EndUtc)> Shifts,
        HashSet<DateOnly> DaysOff,
        ILookup<Guid, RequiredCourse> Requirements,
        Dictionary<Guid, TrainingRecord> Training)
    {
        public static readonly ILookup<Guid, RequiredCourse> NoRequirements = Array.Empty<RequiredCourse>().ToLookup(x => x.PositionId);

        public static readonly WorkerCalendar Empty = new([], [], [], [], NoRequirements, []);
    }

    // A course a position needs, with what deciding "current" and "applies here" takes.
    public sealed record RequiredCourse(Guid PositionId, Guid CourseId, string Name, int? RecurrenceMonths, int? LocationId, bool IsShared);

    // Someone's record on one course: when they last passed it, and the assignment they still have
    // to complete (if any), which is where a "take the course" link goes.
    public sealed record TrainingRecord(DateTime? LatestCompletedUtc, Guid? OpenAssignmentId);

    // The required training for some positions and some people's records on it, for checking many
    // shifts in memory.
    public sealed record TrainingSnapshot(ILookup<Guid, RequiredCourse> Requirements, Dictionary<string, Dictionary<Guid, TrainingRecord>> ByUser)
    {
        public List<TrainingGap> GapsFor(string userId, Shift shift) =>
            TrainingGaps(Requirements, ByUser.GetValueOrDefault(userId) ?? [], shift);
    }

    // Calendars for several people across [fromUtc, toUtc): memberships, positions, active shifts,
    // approved time off and required training - five queries whatever the number of people, six
    // when any of their positions needs training.
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

        TrainingSnapshot training = await LoadTrainingAsync(ctx, ids, positions.Select(x => x.StaffPositionId).ToList());

        return ids.ToDictionary(id => id, id => new WorkerCalendar(
            memberships.Where(x => x.UserId == id).Select(x => x.LocationId).ToHashSet(),
            positions.Where(x => x.UserId == id).Select(x => x.StaffPositionId).ToHashSet(),
            shifts.Where(x => x.UserId == id).Select(x => (x.Id, x.StartUtc, x.EndUtc)).ToList(),
            timeOff.Where(x => x.UserId == id)
                .SelectMany(x => Enumerable.Range(0, x.EndDate.DayNumber - x.StartDate.DayNumber + 1).Select(d => x.StartDate.AddDays(d)))
                .ToHashSet(),
            training.Requirements,
            training.ByUser.GetValueOrDefault(id) ?? []));
    }

    // The training these positions need, and these people's records on it. Only active courses,
    // at companies with Training switched on, count - a course nobody can take can't be required.
    // The second query is skipped when no position needs anything, which is every company that
    // hasn't set requirements up.
    public static async Task<TrainingSnapshot> LoadTrainingAsync(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, IReadOnlyCollection<Guid> positionIds)
    {
        List<Guid> positionList = positionIds.Distinct().ToList();

        if (positionList.Count == 0)
        {
            return new TrainingSnapshot(WorkerCalendar.NoRequirements, []);
        }

        List<RequiredCourse> required = await ctx.StaffPositionRequiredCourses
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => positionList.Contains(x.StaffPositionId) && x.Course!.IsActive
                && !ctx.CompanyFeatureSettings.Any(f => f.CompanyId == x.Course.CompanyId && f.Feature == CompanyFeature.Training && !f.IsEnabled))
            .Select(x => new RequiredCourse(x.StaffPositionId, x.CourseId, x.Course!.Name, x.Course.RecurrenceMonths, x.Course.LocationId, x.Course.IsShared))
            .ToListAsync();

        ILookup<Guid, RequiredCourse> requirements = required.ToLookup(x => x.PositionId);
        List<string> userList = userIds.Distinct().ToList();

        if (required.Count == 0 || userList.Count == 0)
        {
            return new TrainingSnapshot(requirements, []);
        }

        Dictionary<string, Dictionary<Guid, TrainingRecord>> byUser =
            await LoadTrainingRecordsAsync(ctx, userList, required.Select(x => x.CourseId).ToList());

        return new TrainingSnapshot(requirements, byUser);
    }

    // People's records on some courses, by person then course.
    public static async Task<Dictionary<string, Dictionary<Guid, TrainingRecord>>> LoadTrainingRecordsAsync(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, IReadOnlyCollection<Guid> courseIds)
    {
        List<string> userList = userIds.Distinct().ToList();
        List<Guid> courseList = courseIds.Distinct().ToList();

        if (userList.Count == 0 || courseList.Count == 0)
        {
            return [];
        }

        // Every live assignment, not just the latest: a recurring course leaves each completed
        // cycle in place, so the newest completion and the open retake are separate rows.
        var assignments = await ctx.CourseAssignments
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && userList.Contains(x.UserId) && courseList.Contains(x.CourseId))
            .Select(x => new { x.UserId, x.CourseId, x.Id, x.AssignedDate, x.CompletedDate })
            .ToListAsync();

        return assignments
            .GroupBy(x => x.UserId)
            .ToDictionary(user => user.Key, user => user
                .GroupBy(x => x.CourseId)
                .ToDictionary(course => course.Key, course => new TrainingRecord(
                    course.Max(x => x.CompletedDate),
                    course.Where(x => x.CompletedDate == null).OrderByDescending(x => x.AssignedDate).Select(x => (Guid?)x.Id).FirstOrDefault())));
    }

    // The required training for this shift's position that this person isn't current on when the
    // shift starts.
    public static List<TrainingGap> TrainingGaps(WorkerCalendar calendar, Shift shift) =>
        TrainingGaps(calendar.Requirements, calendar.Training, shift);

    private static List<TrainingGap> TrainingGaps(ILookup<Guid, RequiredCourse> requirements, Dictionary<Guid, TrainingRecord> training, Shift shift) =>
        shift.StaffPositionId is Guid positionId
            ? CourseGaps(requirements[positionId], training, shift.LocationId, shift.StartUtc)
            : [];

    // Which of these courses someone isn't current on at atUtc. locationId limits it to courses
    // that apply at that location; null counts every course.
    public static List<TrainingGap> CourseGaps(IEnumerable<RequiredCourse> courses, Dictionary<Guid, TrainingRecord> training, int? locationId, DateTime atUtc)
    {
        List<TrainingGap> gaps = [];

        foreach (RequiredCourse course in courses)
        {
            if (locationId is int at && !AppliesAt(course, at))
            {
                continue;
            }

            TrainingGap? gap = GapFor(course, training.GetValueOrDefault(course.CourseId), atUtc);

            if (gap is not null)
            {
                gaps.Add(gap);
            }
        }

        return gaps;
    }

    // Positions are company-wide but a course can belong to one location. A course that can't be
    // taken at the shift's location (another site's, not shared) doesn't gate shifts there.
    private static bool AppliesAt(RequiredCourse course, int locationId) =>
        course.IsShared || course.LocationId is null || course.LocationId == locationId;

    // Current means passed, and - for a recurring course - not run out by the time the shift
    // starts. Worked out from the completion date rather than the assignment status, because a
    // lapsed cycle still reads "Completed" until the recurrence sweep starts the next one.
    private static TrainingGap? GapFor(RequiredCourse course, TrainingRecord? record, DateTime atUtc)
    {
        if (record?.LatestCompletedUtc is not DateTime completed)
        {
            return new TrainingGap(course.CourseId, course.Name, null, record?.OpenAssignmentId);
        }

        if (course.RecurrenceMonths is not int months)
        {
            return null;
        }

        DateTime runsOut = completed.AddMonths(months);

        return runsOut > atUtc ? null : new TrainingGap(course.CourseId, course.Name, runsOut, record.OpenAssignmentId);
    }

    // "You need Fire Safety and First Aid training to work as Marshal." / "Your Fire Safety
    // training runs out before this shift."
    public static string TrainingReason(IReadOnlyList<TrainingGap> gaps, string? positionName, bool aboutViewer)
    {
        string names = JoinNames(gaps.Select(x => x.CourseName).ToList());

        if (gaps.All(x => x.Lapsed))
        {
            return $"{(aboutViewer ? "Your" : "Their")} {names} training runs out before this shift.";
        }

        return $"{(aboutViewer ? "You need" : "They need")} {names} training to work as {positionName ?? "this position"}.";
    }

    private static string JoinNames(List<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    // Why this person can't work this shift, or null if they can. The same rules as the rota
    // builder: at the location, holding the shift's position (if it has one) and current on its
    // required training, not already working then, and not on approved time off that day.
    // givingUpShiftId is a shift they hand over in the same move (a swap), which doesn't count as
    // a clash. The calendar must cover the shift.
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

        List<TrainingGap> gaps = TrainingGaps(calendar, shift);

        if (gaps.Count > 0)
        {
            return TrainingReason(gaps, shift.StaffPosition?.Name, aboutViewer);
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

    // Members of the shift's location who hold its position and are current on the training it
    // needs - the same rules WhyCantWork applies, so nobody is told about a shift they'd then be
    // refused.
    private static async Task<List<string>> PositionHoldersAtAsync(ApplicationDbContext ctx, Shift shift)
    {
        IQueryable<string> members = ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == shift.LocationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .Select(x => x.UserId);

        if (shift.StaffPositionId is not Guid positionId)
        {
            return await members.Distinct().ToListAsync();
        }

        members = members.Where(userId => ctx.UserPositions.Any(p => p.UserId == userId && p.StaffPositionId == positionId));

        List<string> holders = await members.Distinct().ToListAsync();

        if (holders.Count == 0)
        {
            return holders;
        }

        TrainingSnapshot training = await LoadTrainingAsync(ctx, holders, [positionId]);

        return holders.Where(x => training.GapsFor(x, shift).Count == 0).ToList();
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

    // Tells everyone who could work an open shift that it's there, and puts a card in the
    // location's channel (posted as postedByUserId, the manager who opened it up). The shift needs
    // Location and StaffPosition loaded.
    public static async Task AnnounceOpenShiftAsync(ApplicationDbContext ctx, INotificationDispatcher notifications, Shift shift, IReadOnlyCollection<string> except, string postedByUserId)
    {
        await Chat.ChatChannels.PostCardAsync(ctx, shift.LocationId, ChatMessageKind.OpenShiftCard, shift.Id, postedByUserId,
            $"Open shift: {ShiftClaimNotices.Line(RotaService.ToEmailLine(shift))}", DateTime.UtcNow);

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
