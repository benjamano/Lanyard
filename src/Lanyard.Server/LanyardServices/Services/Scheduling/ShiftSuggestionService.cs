using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// Gathers what ShiftSuggestionRules needs for everyone at the shift's location. Only that
// location's members are ever considered, and their hours elsewhere in the company are only
// summed - never listed - the same as the rota's own hour counts.
public class ShiftSuggestionService(
    IDbContextFactory<ApplicationDbContext> factory,
    IContractRequirementService contractRequirementService,
    ILogger<ShiftSuggestionService> logger) : IShiftSuggestionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IContractRequirementService _contractRequirementService = contractRequirementService;
    private readonly ILogger<ShiftSuggestionService> _logger = logger;

    public async Task<Result<ShiftSuggestions>> SuggestAsync(LocationScope scope, Shift requested)
    {
        // A copy, so the caller's shift isn't changed (the position is attached below), and so it
        // is always treated as nobody's yet.
        Shift shift = new()
        {
            Id = requested.Id,
            LocationId = requested.LocationId,
            StaffPositionId = requested.StaffPositionId,
            StartUtc = requested.StartUtc,
            EndUtc = requested.EndUtc,
            BreakMinutes = requested.BreakMinutes,
            CreateByUserId = requested.CreateByUserId
        };

        try
        {
            string? timeError = RotaService.ValidateTimes(shift.StartUtc, shift.EndUtc, shift.BreakMinutes);

            if (timeError is not null)
            {
                return Result<ShiftSuggestions>.Fail(timeError);
            }

            if (!SchedulingAccess.CanManageLocation(scope, shift.LocationId))
            {
                return Result<ShiftSuggestions>.Fail("You can only see suggestions for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == shift.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<ShiftSuggestions>.Fail("Location not found.");
            }

            int companyId = location.CompanyId;

            // The reasons name the position ("They don't work as Marshal."), so it's looked up
            // rather than trusted from the caller - and must be this company's.
            if (shift.StaffPositionId is Guid positionId)
            {
                StaffPosition? position = await ctx.StaffPositions
                    .AsNoTracking()
                    .TagWithCallSite()
                    .FirstOrDefaultAsync(x => x.Id == positionId && x.CompanyId == companyId);

                if (position is null)
                {
                    return Result<ShiftSuggestions>.Fail("That position wasn't found.");
                }

                shift.StaffPosition = position;
            }

            // The shift's week, plus a day either side so a late finish the night before (or an
            // early start the morning after) is seen by the rest check.
            DateOnly day = RotaTime.LocalDate(shift.StartUtc);

            SuggestionPool pool = await SuggestionPool.LoadAsync(ctx, _contractRequirementService, _logger, shift.LocationId, companyId, day, day);

            if (pool.Users.Count == 0)
            {
                return Result<ShiftSuggestions>.Ok(ShiftSuggestions.Empty);
            }

            HashSet<string> claimants = shift.Id == Guid.Empty
                ? []
                : (await ctx.ShiftClaims
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Open()
                    .Where(x => x.ShiftId == shift.Id && x.Kind == ShiftClaimKind.Pickup)
                    .Select(x => x.UserId)
                    .ToListAsync()).ToHashSet();

            List<SuggestionCandidate> candidates = pool.Users.Select(user =>
            {
                StaffPosition? primary = pool.PrimaryPositions.GetValueOrDefault(user.Id);

                return new SuggestionCandidate(
                    user.Id,
                    RotaNames.For(user),
                    primary?.Id,
                    primary?.Name,
                    pool.ContractFor(user.Id),
                    pool.ShiftsByUser[user.Id].ToList(),
                    pool.PendingTimeOffOn(user.Id, day),
                    claimants.Contains(user.Id),
                    ShiftClaimRules.WhyCantWork(pool.CalendarFor(user.Id), shift, null, aboutViewer: false),
                    pool.Availability[user.Id].ToList());
            }).ToList();

            return Result<ShiftSuggestions>.Ok(ShiftSuggestionRules.Rank(shift, candidates));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suggest staff for a shift at {LocationId}", shift.LocationId);
            return Result<ShiftSuggestions>.Fail($"Failed to suggest staff: {ex.Message}");
        }
    }
}

// Everything the suggestion ranking needs about a location's people for some days, loaded in a
// fixed number of queries whatever the number of people or shifts: the one-shift suggestions and
// Auto-build (many shifts at once) share it. Shifts are each person's active shifts at any of the
// company's locations from the day before the first day's week to the day after the last day's
// week (the weekly hours and the rest check need them); calendars cover the days themselves, a day
// either side.
internal sealed record SuggestionPool(
    List<UserProfile> Users,
    Dictionary<string, ShiftClaimRules.WorkerCalendar> Calendars,
    ILookup<string, Shift> ShiftsByUser,
    Dictionary<string, ResolvedContract> Contracts,
    ILookup<string, TimeOffRequest> PendingTimeOff,
    Dictionary<string, StaffPosition> PrimaryPositions,
    ILookup<string, StaffAvailability> Availability)
{
    public ResolvedContract ContractFor(string userId) => Contracts.GetValueOrDefault(userId) ?? ResolvedContract.Empty;

    public ShiftClaimRules.WorkerCalendar CalendarFor(string userId) =>
        Calendars.GetValueOrDefault(userId, ShiftClaimRules.WorkerCalendar.Empty);

    // The type of an undecided time-off request covering that day ("Holiday"), or null.
    public string? PendingTimeOffOn(string userId, DateOnly day) =>
        PendingTimeOff[userId].FirstOrDefault(x => x.Covers(day)) is TimeOffRequest request
            ? request.TimeOffType?.Name ?? "time off"
            : null;

    public static async Task<SuggestionPool> LoadAsync(
        ApplicationDbContext ctx, IContractRequirementService contractRequirementService, ILogger logger,
        int locationId, int companyId, DateOnly firstDay, DateOnly lastDay)
    {
        List<string> memberIds = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync();

        List<UserProfile> users = await ctx.Users
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => memberIds.Contains(x.Id))
            .ToListAsync();

        if (users.Count == 0)
        {
            return new SuggestionPool([], [], Array.Empty<Shift>().ToLookup(x => x.UserId!), [],
                Array.Empty<TimeOffRequest>().ToLookup(x => x.UserId), [], Array.Empty<StaffAvailability>().ToLookup(x => x.UserId));
        }

        List<string> userIds = users.Select(x => x.Id).ToList();

        DateTime fromUtc = RotaTime.StartOfDayUtc(RotaTime.GetWeekStart(firstDay).AddDays(-1));
        DateTime toUtc = RotaTime.StartOfDayUtc(RotaTime.GetWeekStart(lastDay).AddDays(8));

        Dictionary<string, ShiftClaimRules.WorkerCalendar> calendars = await ShiftClaimRules.LoadCalendarsAsync(
            ctx, userIds, RotaTime.StartOfDayUtc(firstDay.AddDays(-1)), RotaTime.StartOfDayUtc(lastDay.AddDays(2)));

        List<int> companyLocationIds = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId)
            .Select(x => x.Id)
            .ToListAsync();

        ILookup<string, Shift> shiftsByUser = (await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && x.UserId != null && userIds.Contains(x.UserId)
                && companyLocationIds.Contains(x.LocationId)
                && x.StartUtc < toUtc && x.EndUtc > fromUtc)
            .ToListAsync())
            .ToLookup(x => x.UserId!);

        Result<Dictionary<string, ResolvedContract>> contractsResult =
            await contractRequirementService.ResolveForUsersAsync(userIds, companyId);

        Dictionary<string, ResolvedContract> contracts = contractsResult.IsSuccess && contractsResult.Data is not null
            ? contractsResult.Data
            : [];

        if (!contractsResult.IsSuccess)
        {
            logger.LogWarning("Failed to resolve contracts for shift suggestions at {LocationId}: {Error}", locationId, contractsResult.Error);
        }

        // Approved time off already rules someone out (WhyCantWork); an undecided request is
        // only a caution.
        ILookup<string, TimeOffRequest> pendingTimeOff = (await TimeOffService.LiveTimeOffQuery(ctx, userIds, firstDay, lastDay)
            .Where(x => x.Status == TimeOffStatus.Pending)
            .ToListAsync())
            .ToLookup(x => x.UserId);

        Dictionary<string, StaffPosition> primaryPositions = (await ctx.UserPositions
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.StaffPosition)
            .Where(x => userIds.Contains(x.UserId) && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
            .ToListAsync())
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.First().StaffPosition!);

        ILookup<string, StaffAvailability> availabilityByUser =
            (await StaffAvailabilityService.ForUsers(ctx, userIds, companyId).ToListAsync()).ToLookup(x => x.UserId);

        return new SuggestionPool(users, calendars, shiftsByUser, contracts, pendingTimeOff, primaryPositions, availabilityByUser);
    }
}
