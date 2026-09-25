using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lanyard.Application.Services.Scheduling;

public class TimeOffService(
    IDbContextFactory<ApplicationDbContext> factory,
    ISchedulingSettingsService settingsService,
    ITimeOffEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<TimeOffService> logger) : ITimeOffService
{
    private const int MaxNotesLength = 500;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ISchedulingSettingsService _settingsService = settingsService;
    private readonly ITimeOffEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<TimeOffService> _logger = logger;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private DateOnly Today => RotaTime.Today(UtcNow);

    public async Task<Result<TimeOffBalances>> GetBalancesAsync(string userId, int companyId, DateOnly onDate)
    {
        try
        {
            CompanySchedulingSettings settings = await GetSettingsAsync(companyId);
            HolidayYear year = HolidayYear.For(settings, onDate);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // A company's types are created on first use; a balance can be the first thing asked
            // for (the Time off tab loads it before the types list), so make sure they exist.
            await TimeOffPolicyService.EnsureDefaultTypesAsync(ctx, companyId, _logger);

            List<TimeOffBalance> balances = await BalancesForYearAsync(ctx, userId, companyId, year);

            return Result<TimeOffBalances>.Ok(new TimeOffBalances(year, settings.HoursPerDay, balances));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to work out your time-off balance");
            return Result<TimeOffBalances>.Fail($"Failed to work out your time-off balance: {ex.Message}");
        }
    }

    public async Task<Result<TimeOffPreview>> PreviewAsync(string userId, int companyId, TimeOffRequestDraft draft, bool forManager = false)
    {
        try
        {
            CompanySchedulingSettings settings = await GetSettingsAsync(companyId);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (string? error, DraftContext? context) = await ValidateDraftAsync(ctx, userId, companyId, settings, draft, recordedByManager: forManager);

            if (error is not null || context is null)
            {
                return Result<TimeOffPreview>.Fail(error ?? "That request isn't valid.");
            }

            return Result<TimeOffPreview>.Ok(new TimeOffPreview(
                context.Year,
                settings.HoursPerDay,
                (draft.EndDate.DayNumber - draft.StartDate.DayNumber + 1) * settings.HoursPerDay,
                context.Balance,
                context.Warnings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check the request");
            return Result<TimeOffPreview>.Fail($"Failed to check the request: {ex.Message}");
        }
    }

    public async Task<Result<TimeOffSubmitResult>> SubmitAsync(string userId, int locationId, TimeOffRequestDraft draft) =>
        await CreateAsync(userId, locationId, draft, requestedBy: userId, recordedByManager: false);

    public async Task<Result<TimeOffSubmitResult>> RecordForUserAsync(LocationScope scope, string userId, int locationId, TimeOffRequestDraft draft, string managerUserId)
    {
        if (!SchedulingAccess.CanManageLocation(scope, locationId))
        {
            return Result<TimeOffSubmitResult>.Fail("You can only record time off for people at your own location.");
        }

        // Recording saves straight as approved, so recording your own would skip approval.
        if (userId == managerUserId && !scope.IsAdmin)
        {
            return Result<TimeOffSubmitResult>.Fail("You can't record your own time off. Request it from My Shifts, or ask another manager.");
        }

        return await CreateAsync(userId, locationId, draft, requestedBy: managerUserId, recordedByManager: true);
    }

    public async Task<Result<bool>> CancelAsync(string userId, Guid requestId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeOffRequest? request = await ctx.TimeOffRequests.FirstOrDefaultAsync(x => x.Id == requestId && x.UserId == userId);

            if (request is null)
            {
                return Result<bool>.Fail("That request no longer exists.");
            }

            switch (request.Status)
            {
                case TimeOffStatus.Pending:
                    break;
                case TimeOffStatus.Approved when request.StartDate > Today:
                    break;
                case TimeOffStatus.Approved:
                    return Result<bool>.Fail("This time off has already started. Ask your manager to change it.");
                default:
                    return Result<bool>.Fail("Only pending or upcoming approved requests can be cancelled.");
            }

            request.Status = TimeOffStatus.Cancelled;
            request.CancelledDateUtc = UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("User {UserId} cancelled time-off request {RequestId}", userId, requestId);
            _eventBus.Publish(request.LocationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel the request");
            return Result<bool>.Fail($"Failed to cancel the request: {ex.Message}");
        }
    }

    public async Task<Result<List<TimeOffRequest>>> GetRequestsForUserAsync(string userId, DateOnly fromDate)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TimeOffRequest> requests = await ctx.TimeOffRequests
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.TimeOffType)
                .Where(x => x.UserId == userId && x.EndDate >= fromDate)
                .OrderBy(x => x.StartDate)
                .ToListAsync();

            return Result<List<TimeOffRequest>>.Ok(requests);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve your time off");
            return Result<List<TimeOffRequest>>.Fail($"Failed to retrieve your time off: {ex.Message}");
        }
    }

    public async Task<Result<List<TimeOffRequestView>>> GetRequestsForLocationAsync(LocationScope scope, int locationId, TimeOffListFilter filter)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<TimeOffRequestView>>.Fail("You can only see time off for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == locationId);

            if (location is null)
            {
                return Result<List<TimeOffRequestView>>.Fail("Location not found.");
            }

            DateOnly today = Today;

            IQueryable<TimeOffRequest> query = ctx.TimeOffRequests
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.TimeOffType)
                .Include(x => x.User)
                .Where(x => x.LocationId == locationId);

            query = filter switch
            {
                TimeOffListFilter.Pending => query.Where(x => x.Status == TimeOffStatus.Pending).OrderBy(x => x.StartDate),
                TimeOffListFilter.Upcoming => query.Where(x => x.Status == TimeOffStatus.Approved && x.EndDate >= today).OrderBy(x => x.StartDate),
                _ => query.Where(x => x.EndDate >= today.AddDays(-90)).OrderByDescending(x => x.StartDate)
            };

            List<TimeOffRequest> requests = await query.Take(200).ToListAsync();

            CompanySchedulingSettings settings = await GetSettingsAsync(location.CompanyId);

            // Everyone else off at this location around these dates, for the "who else is off"
            // column - one query for the whole list rather than one per request.
            List<TimeOffRequest> others = [];

            if (requests.Count > 0)
            {
                DateOnly minStart = requests.Min(x => x.StartDate);
                DateOnly maxEnd = requests.Max(x => x.EndDate);

                others = await ctx.TimeOffRequests
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Include(x => x.User)
                    .Where(x => x.LocationId == locationId
                        && (x.Status == TimeOffStatus.Approved || x.Status == TimeOffStatus.Pending)
                        && x.StartDate <= maxEnd && x.EndDate >= minStart)
                    .ToListAsync();
            }

            // Balances and rota days for everyone in the list are fetched in a fixed number of
            // queries up front, not per request.
            List<string> requesterIds = requests.Select(x => x.UserId).Distinct().ToList();
            List<HolidayYear> years = requests.Select(x => HolidayYear.For(settings, x.StartDate)).Distinct().ToList();

            Dictionary<(string UserId, DateOnly YearStart), List<TimeOffBalance>> balancesByUserYear = requests.Count > 0
                ? await BalancesForUsersAsync(ctx, location.CompanyId, requesterIds, years)
                : [];

            ILookup<string, DateOnly> shiftDaysByUser = requests.Count > 0
                ? await ShiftDaysForUsersAsync(ctx, requesterIds, requests.Min(x => x.StartDate), requests.Max(x => x.EndDate))
                : Enumerable.Empty<(string, DateOnly)>().ToLookup(x => x.Item1, x => x.Item2);

            List<TimeOffRequestView> views = [];

            foreach (TimeOffRequest request in requests)
            {
                HolidayYear year = HolidayYear.For(settings, request.StartDate);

                TimeOffBalance? balance = balancesByUserYear.GetValueOrDefault((request.UserId, year.Start))?
                    .FirstOrDefault(x => x.Type.Id == request.TimeOffTypeId);
                string name = RotaNames.For(request.User);
                List<string> warnings = [];

                // For a pending request the question is "can they afford this one?", so its own
                // hours come off what's left; an approved request is already inside ApprovedHours.
                if (balance is not null && request.Status == TimeOffStatus.Pending && balance.RemainingHours is decimal remaining && remaining - request.Hours < 0)
                {
                    warnings.Add($"Approving this puts {name} {TimeOffFormat.DaysAndHours(request.Hours - remaining, settings.HoursPerDay)} over their {balance.Type.Name.ToLower()} allowance for {year.Label}.");
                }
                else if (balance is not null && request.Status == TimeOffStatus.Approved && balance.RemainingHours is decimal left && left < 0)
                {
                    warnings.Add($"{name} is {TimeOffFormat.DaysAndHours(-left, settings.HoursPerDay)} over their {balance.Type.Name.ToLower()} allowance for {year.Label}.");
                }

                if (balance is not null && !balance.Allowance.IsConfigured && request.Status == TimeOffStatus.Pending)
                {
                    warnings.Add($"No {balance.Type.Name.ToLower()} allowance is set for {name}.");
                }

                if (request.Status is TimeOffStatus.Pending or TimeOffStatus.Approved)
                {
                    List<DateOnly> shiftDays = shiftDaysByUser[request.UserId].Where(request.Covers).ToList();

                    if (shiftDays.Count > 0)
                    {
                        warnings.Add($"{name} is on the rota on {DayList(shiftDays)}.");
                    }
                }

                List<string> othersOff = others
                    .Where(x => x.Id != request.Id && x.UserId != request.UserId && x.Overlaps(request.StartDate, request.EndDate))
                    .Select(x => RotaNames.For(x.User) + (x.Status == TimeOffStatus.Pending ? " (requested)" : string.Empty))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                views.Add(new TimeOffRequestView(request, name, settings.HoursPerDay, balance, warnings, othersOff));
            }

            return Result<List<TimeOffRequestView>>.Ok(views);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve time-off requests");
            return Result<List<TimeOffRequestView>>.Fail($"Failed to retrieve time-off requests: {ex.Message}");
        }
    }

    public async Task<Result<int>> CountPendingAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<int>.Ok(0);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int count = await ctx.TimeOffRequests
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => x.LocationId == locationId && x.Status == TimeOffStatus.Pending);

            return Result<int>.Ok(count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count pending requests");
            return Result<int>.Fail($"Failed to count pending requests: {ex.Message}");
        }
    }

    public async Task<Result<int>> CountPendingForNavAsync(LocationScope scope, string? viewerUserId)
    {
        try
        {
            if (!scope.IsAdmin && !scope.IsManager)
            {
                return Result<int>.Ok(0);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<TimeOffRequest> pending = ctx.TimeOffRequests
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Status == TimeOffStatus.Pending);

            // A manager's own request is waiting on someone else, not on them.
            if (!scope.IsAdmin && viewerUserId is not null)
            {
                pending = pending.Where(x => x.UserId != viewerUserId);
            }

            // The location they signed in to - the one the Time Off Requests page opens on. Only an
            // Admin with no location of their own sees every location's total.
            if (scope.LocationId is int locationId)
            {
                pending = pending.Where(x => x.LocationId == locationId);
            }
            else if (!scope.IsAdmin)
            {
                return Result<int>.Ok(0);
            }

            return Result<int>.Ok(await pending.CountAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count pending time-off requests for the nav");
            return Result<int>.Fail($"Failed to count pending requests: {ex.Message}");
        }
    }

    public async Task<Result<TimeOffRequest>> DecideAsync(LocationScope scope, Guid requestId, bool approve, string? reason, string deciderUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeOffRequest? request = await ctx.TimeOffRequests
                .Include(x => x.TimeOffType)
                .FirstOrDefaultAsync(x => x.Id == requestId);

            if (request is null)
            {
                return Result<TimeOffRequest>.Fail("That request no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, request.LocationId))
            {
                return Result<TimeOffRequest>.Fail("You can only decide time off for your own location.");
            }

            // Nobody approves their own leave. Admins are the exception, since someone has to be
            // able to decide the most senior person's.
            if (request.UserId == deciderUserId && !scope.IsAdmin)
            {
                return Result<TimeOffRequest>.Fail("You can't decide your own time off. Another manager or an admin needs to.");
            }

            string? trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

            if (trimmedReason?.Length > MaxNotesLength)
            {
                return Result<TimeOffRequest>.Fail("Keep the reason to 500 characters or fewer.");
            }

            if (approve)
            {
                if (request.Status != TimeOffStatus.Pending)
                {
                    return Result<TimeOffRequest>.Fail("Only a pending request can be approved.");
                }

                request.Status = TimeOffStatus.Approved;
            }
            else
            {
                DateOnly today = Today;

                // An approved request can be withdrawn while any of it is still to come. Once only
                // today (or nothing) is left, it has been taken.
                bool canReject = request.Status == TimeOffStatus.Pending
                    || (request.Status == TimeOffStatus.Approved && request.EndDate > today);

                if (!canReject)
                {
                    return Result<TimeOffRequest>.Fail(request.Status == TimeOffStatus.Approved
                        ? "This time off has already been taken, so there's nothing left to withdraw."
                        : "This request can't be rejected any more.");
                }

                if (trimmedReason is null)
                {
                    return Result<TimeOffRequest>.Fail("Give a reason - the person will see it.");
                }

                if (request.Status == TimeOffStatus.Approved && request.StartDate <= today)
                {
                    // Already under way: the days up to and including today stay as taken, and
                    // only the rest is withdrawn - recorded as its own rejected request so the
                    // person sees exactly which days were cancelled and why.
                    TimeOffRequest withdrawn = CutShort(request, today, trimmedReason, deciderUserId);
                    ctx.TimeOffRequests.Add(withdrawn);

                    await ctx.SaveChangesAsync();

                    _logger.LogInformation("Time-off request {RequestId} cut short after {Today} by {DeciderUserId}", requestId, today, deciderUserId);
                    _eventBus.Publish(request.LocationId);

                    return Result<TimeOffRequest>.Ok(withdrawn);
                }

                request.Status = TimeOffStatus.Rejected;
            }

            request.DecidedByUserId = deciderUserId;
            request.DecidedDateUtc = UtcNow;
            request.DecisionReason = trimmedReason;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Time-off request {RequestId} {Decision} by {DeciderUserId}", requestId, approve ? "approved" : "rejected", deciderUserId);
            _eventBus.Publish(request.LocationId);

            return Result<TimeOffRequest>.Ok(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save the decision");
            return Result<TimeOffRequest>.Fail($"Failed to save the decision: {ex.Message}");
        }
    }

    public async Task<Result<List<TimeOffRequest>>> GetLiveTimeOffAsync(IReadOnlyCollection<string> userIds, DateOnly from, DateOnly to)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<List<TimeOffRequest>>.Ok(await LiveTimeOffQuery(ctx, userIds, from, to).ToListAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve time off");
            return Result<List<TimeOffRequest>>.Fail($"Failed to retrieve time off: {ex.Message}");
        }
    }

    // Splits an approved request that has started: the original keeps the days up to and including
    // today (with a proportional share of the hours), and a new Rejected request holds the rest.
    private TimeOffRequest CutShort(TimeOffRequest request, DateOnly today, string reason, string deciderUserId)
    {
        int totalDays = request.DayCount;
        int keptDays = today.DayNumber - request.StartDate.DayNumber + 1;
        decimal keptHours = Math.Round(request.Hours * keptDays / totalDays, 2);

        TimeOffRequest withdrawn = new()
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            TimeOffTypeId = request.TimeOffTypeId,
            LocationId = request.LocationId,
            StartDate = today.AddDays(1),
            EndDate = request.EndDate,
            Hours = request.Hours - keptHours,
            Notes = request.Notes,
            Status = TimeOffStatus.Rejected,
            RequestedDateUtc = request.RequestedDateUtc,
            RequestedByUserId = request.RequestedByUserId,
            DecidedByUserId = deciderUserId,
            DecidedDateUtc = UtcNow,
            DecisionReason = reason,
            TimeOffType = request.TimeOffType
        };

        request.EndDate = today;
        request.Hours = keptHours;

        return withdrawn;
    }

    // Shared with RotaService, which reads time off inside its own context.
    internal static IQueryable<TimeOffRequest> LiveTimeOffQuery(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, DateOnly from, DateOnly to) =>
        ctx.TimeOffRequests
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.TimeOffType)
            .Where(x => userIds.Contains(x.UserId)
                && (x.Status == TimeOffStatus.Approved || x.Status == TimeOffStatus.Pending)
                && x.StartDate <= to && x.EndDate >= from)
            .OrderBy(x => x.StartDate);

    private async Task<Result<TimeOffSubmitResult>> CreateAsync(string userId, int locationId, TimeOffRequestDraft draft, string requestedBy, bool recordedByManager)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<TimeOffSubmitResult>.Fail("Location not found.");
            }

            bool isMember = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == userId && x.LocationId == locationId);

            if (!isMember)
            {
                return Result<TimeOffSubmitResult>.Fail(recordedByManager
                    ? "That person isn't a member of this location."
                    : "You're not a member of this location.");
            }

            CompanySchedulingSettings settings = await GetSettingsAsync(location.CompanyId);

            (string? error, DraftContext? context) = await ValidateDraftAsync(ctx, userId, location.CompanyId, settings, draft, recordedByManager);

            if (error is not null || context is null)
            {
                return Result<TimeOffSubmitResult>.Fail(error ?? "That request isn't valid.");
            }

            DateTime now = UtcNow;

            TimeOffRequest request = new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TimeOffTypeId = draft.TimeOffTypeId,
                LocationId = locationId,
                StartDate = draft.StartDate,
                EndDate = draft.EndDate,
                Hours = Math.Round(draft.Hours, 2),
                Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
                Status = recordedByManager ? TimeOffStatus.Approved : TimeOffStatus.Pending,
                RequestedDateUtc = now,
                RequestedByUserId = requestedBy,
                DecidedByUserId = recordedByManager ? requestedBy : null,
                DecidedDateUtc = recordedByManager ? now : null
            };

            ctx.TimeOffRequests.Add(request);

            try
            {
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
            {
                // Two requests for the same days raced past the overlap check above; the
                // exclusion constraint on TimeOffRequests kept only the first.
                _logger.LogWarning("Overlapping time-off request for {UserId} refused by the database: {Error}", userId, ex.InnerException.Message);
                return Result<TimeOffSubmitResult>.Fail(recordedByManager
                    ? "They already have time off booked or requested for some of those days."
                    : "You already have time off booked or requested for some of those days.");
            }

            request.TimeOffType = context.Type;
            _eventBus.Publish(locationId);

            _logger.LogInformation(recordedByManager
                    ? "Manager {RequestedBy} recorded time off {RequestId} for {UserId}"
                    : "User {RequestedBy} requested time off {RequestId} for {UserId}",
                requestedBy, request.Id, userId);

            return Result<TimeOffSubmitResult>.Ok(new TimeOffSubmitResult(request, context.Warnings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save the request");
            return Result<TimeOffSubmitResult>.Fail($"Failed to save the request: {ex.Message}");
        }
    }

    private sealed record DraftContext(TimeOffType Type, HolidayYear Year, TimeOffBalance? Balance, List<string> Warnings);

    // Every rule a request has to pass, shared by preview, submit and manager recording so the
    // dialog's live check can never disagree with what saving does.
    private async Task<(string? Error, DraftContext? Context)> ValidateDraftAsync(
        ApplicationDbContext ctx, string userId, int companyId, CompanySchedulingSettings settings, TimeOffRequestDraft draft, bool recordedByManager)
    {
        TimeOffType? type = await ctx.TimeOffTypes.AsNoTracking().TagWithCallSite()
            .FirstOrDefaultAsync(x => x.Id == draft.TimeOffTypeId && x.CompanyId == companyId && x.IsActive);

        if (type is null)
        {
            return ("Choose a type of time off.", null);
        }

        if (draft.EndDate < draft.StartDate)
        {
            return ("The last day can't be before the first day.", null);
        }

        if (!recordedByManager && draft.StartDate < Today)
        {
            return ("Time off can't start in the past. Ask your manager to record it for you.", null);
        }

        int days = draft.EndDate.DayNumber - draft.StartDate.DayNumber + 1;

        if (draft.Hours <= 0)
        {
            return ("The hours must be more than zero.", null);
        }

        if (draft.Hours > days * 24)
        {
            return ($"{RotaFormat.Hours(draft.Hours)} is more hours than there are in {days} {(days == 1 ? "day" : "days")}.", null);
        }

        if (draft.Notes?.Trim().Length > MaxNotesLength)
        {
            return ("Keep the notes to 500 characters or fewer.", null);
        }

        HolidayYear year = HolidayYear.For(settings, draft.StartDate);

        if (!year.Contains(draft.EndDate))
        {
            DateOnly nextStart = year.End.AddDays(1);

            return ($"This runs into the next holiday year, which starts on {nextStart.ToString("d MMMM", RotaFormat.Uk)}. " +
                    $"Make one request up to {year.End.ToString("d MMMM", RotaFormat.Uk)} and another from {nextStart.ToString("d MMMM", RotaFormat.Uk)}.", null);
        }

        TimeOffRequest? clash = await ctx.TimeOffRequests.AsNoTracking().TagWithCallSite()
            .Where(x => x.UserId == userId
                && (x.Status == TimeOffStatus.Pending || x.Status == TimeOffStatus.Approved)
                && x.StartDate <= draft.EndDate && x.EndDate >= draft.StartDate)
            .OrderBy(x => x.StartDate)
            .FirstOrDefaultAsync();

        if (clash is not null)
        {
            string status = clash.Status == TimeOffStatus.Approved ? "booked" : "requested";
            string who = recordedByManager ? "They already have" : "You already have";

            return ($"{who} time off {status} for {TimeOffFormat.DateRange(clash.StartDate, clash.EndDate)}.", null);
        }

        List<string> warnings = [];
        TimeOffBalance? balance = null;

        if (type.DeductsFromAllowance)
        {
            List<TimeOffBalance> balances = await BalancesForYearAsync(ctx, userId, companyId, year);
            balance = balances.FirstOrDefault(x => x.Type.Id == type.Id);

            if (balance is not null && !balance.Allowance.IsConfigured)
            {
                warnings.Add(recordedByManager
                    ? $"No {type.Name.ToLower()} allowance is set for this person."
                    : $"No {type.Name.ToLower()} allowance has been set for you yet. Your manager will check it.");
            }
            // Staff are warned against everything they've asked for, pending included. A manager
            // recording time off approves it outright, so only what's already approved counts.
            else if ((recordedByManager ? balance?.RemainingHours : balance?.RemainingAfterPendingHours) is decimal left && left - draft.Hours < 0)
            {
                decimal over = draft.Hours - left;
                string pendingNote = balance.PendingHours > 0 ? ", counting your other pending requests" : string.Empty;

                warnings.Add(recordedByManager
                    ? $"This puts them {TimeOffFormat.DaysAndHours(over, settings.HoursPerDay)} over their {type.Name.ToLower()} allowance for {year.Label}."
                    : $"This is {TimeOffFormat.DaysAndHours(over, settings.HoursPerDay)} more than you have left for {year.Label}{pendingNote}. Your manager can still approve it.");
            }
        }

        List<DateOnly> shiftDays = await ShiftDaysAsync(ctx, userId, draft.StartDate, draft.EndDate);

        if (shiftDays.Count > 0)
        {
            warnings.Add(recordedByManager
                ? $"They're on the rota on {DayList(shiftDays)}. Remember to cover those shifts."
                : $"You're on the rota on {DayList(shiftDays)}. Your manager will sort out cover if this is approved.");
        }

        return (null, new DraftContext(type, year, balance, warnings));
    }

    private static async Task<List<TimeOffBalance>> BalancesForYearAsync(ApplicationDbContext ctx, string userId, int companyId, HolidayYear year)
    {
        Dictionary<(string UserId, DateOnly YearStart), List<TimeOffBalance>> all = await BalancesForUsersAsync(ctx, companyId, [userId], [year]);

        return all[(userId, year.Start)];
    }

    // Balances for many people and holiday years in a fixed number of queries: types, allowances
    // (via the batched resolver) and one grouped read of live requests.
    private static async Task<Dictionary<(string UserId, DateOnly YearStart), List<TimeOffBalance>>> BalancesForUsersAsync(
        ApplicationDbContext ctx, int companyId, IReadOnlyCollection<string> userIds, IReadOnlyCollection<HolidayYear> years)
    {
        List<TimeOffType> types = await ctx.TimeOffTypes
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId && x.IsActive && x.DeductsFromAllowance)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync();

        Dictionary<string, Dictionary<Guid, ResolvedAllowance>> allowances =
            await TimeOffPolicyService.ResolveForUsersCoreAsync(ctx, companyId, userIds);

        List<string> ids = userIds.Distinct().ToList();
        List<Guid> typeIds = types.Select(x => x.Id).ToList();
        DateOnly from = years.Min(x => x.Start);
        DateOnly to = years.Max(x => x.End);

        var live = await ctx.TimeOffRequests
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.UserId)
                && typeIds.Contains(x.TimeOffTypeId)
                && (x.Status == TimeOffStatus.Approved || x.Status == TimeOffStatus.Pending)
                && x.StartDate >= from && x.StartDate <= to)
            .Select(x => new { x.UserId, x.TimeOffTypeId, x.Status, x.StartDate, x.Hours })
            .ToListAsync();

        Dictionary<(string UserId, DateOnly YearStart), List<TimeOffBalance>> result = [];

        foreach (string userId in ids)
        {
            Dictionary<Guid, ResolvedAllowance> userAllowances = allowances.GetValueOrDefault(userId) ?? [];

            foreach (HolidayYear year in years)
            {
                var inYear = live.Where(x => x.UserId == userId && year.Contains(x.StartDate)).ToList();

                result[(userId, year.Start)] = types.Select(type => new TimeOffBalance(
                        type,
                        userAllowances.GetValueOrDefault(type.Id) ?? ResolvedAllowance.None(type.Id),
                        inYear.Where(x => x.TimeOffTypeId == type.Id && x.Status == TimeOffStatus.Approved).Sum(x => x.Hours),
                        inYear.Where(x => x.TimeOffTypeId == type.Id && x.Status == TimeOffStatus.Pending).Sum(x => x.Hours)))
                    .ToList();
            }
        }

        return result;
    }

    // Days in [from, to] on which the person has a published, active shift anywhere.
    private static async Task<List<DateOnly>> ShiftDaysAsync(ApplicationDbContext ctx, string userId, DateOnly from, DateOnly to)
    {
        DateTime fromUtc = RotaTime.StartOfDayUtc(from);
        DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        List<DateTime> starts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.IsActive && x.PublishedDateUtc != null && x.StartUtc >= fromUtc && x.StartUtc < toUtc)
            .Select(x => x.StartUtc)
            .ToListAsync();

        return starts.Select(RotaTime.LocalDate).Distinct().Order().ToList();
    }

    // Local dates of published, active shifts for several people across [from, to], in one query.
    private static async Task<ILookup<string, DateOnly>> ShiftDaysForUsersAsync(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, DateOnly from, DateOnly to)
    {
        DateTime fromUtc = RotaTime.StartOfDayUtc(from);
        DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        var shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => userIds.Contains(x.UserId) && x.IsActive && x.PublishedDateUtc != null && x.StartUtc >= fromUtc && x.StartUtc < toUtc)
            .Select(x => new { x.UserId, x.StartUtc })
            .ToListAsync();

        return shifts
            .Select(x => (x.UserId, Day: RotaTime.LocalDate(x.StartUtc)))
            .Distinct()
            .OrderBy(x => x.Day)
            .ToLookup(x => x.UserId, x => x.Day);
    }

    private static string DayList(List<DateOnly> days)
    {
        List<string> labels = days.Take(3).Select(d => d.ToString("ddd d MMM", RotaFormat.Uk)).ToList();
        string text = string.Join(", ", labels);

        return days.Count > 3 ? $"{text} and {days.Count - 3} more {(days.Count - 3 == 1 ? "day" : "days")}" : text;
    }

    private async Task<CompanySchedulingSettings> GetSettingsAsync(int companyId)
    {
        Result<CompanySchedulingSettings> result = await _settingsService.GetSettingsAsync(companyId);

        if (!result.IsSuccess || result.Data is null)
        {
            _logger.LogWarning("Falling back to default scheduling settings for company {CompanyId}: {Error}", companyId, result.Error);
            return new CompanySchedulingSettings { CompanyId = companyId };
        }

        return result.Data;
    }
}
