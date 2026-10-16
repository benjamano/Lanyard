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

            List<string> memberIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == shift.LocationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
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
                return Result<ShiftSuggestions>.Ok(ShiftSuggestions.Empty);
            }

            List<string> userIds = users.Select(x => x.Id).ToList();

            // The shift's week, plus a day either side so a late finish the night before (or an
            // early start the morning after) is seen by the rest check.
            DateOnly day = RotaTime.LocalDate(shift.StartUtc);
            DateOnly weekStart = RotaTime.GetWeekStart(day);
            DateTime fromUtc = RotaTime.StartOfDayUtc(weekStart.AddDays(-1));
            DateTime toUtc = RotaTime.StartOfDayUtc(weekStart.AddDays(8));

            Dictionary<string, ShiftClaimRules.WorkerCalendar> calendars =
                await ShiftClaimRules.LoadCalendarsAsync(ctx, userIds, shift.StartUtc.AddDays(-1), shift.EndUtc.AddDays(1));

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
                await _contractRequirementService.ResolveForUsersAsync(userIds, companyId);

            Dictionary<string, ResolvedContract> contracts = contractsResult.IsSuccess && contractsResult.Data is not null
                ? contractsResult.Data
                : [];

            if (!contractsResult.IsSuccess)
            {
                _logger.LogWarning("Failed to resolve contracts for shift suggestions at {LocationId}: {Error}", shift.LocationId, contractsResult.Error);
            }

            // Approved time off already rules someone out (WhyCantWork); an undecided request is
            // only a caution.
            Dictionary<string, string> pendingTimeOff = (await TimeOffService.LiveTimeOffQuery(ctx, userIds, day, day)
                .Where(x => x.Status == TimeOffStatus.Pending)
                .ToListAsync())
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.First().TimeOffType?.Name ?? "time off");

            HashSet<string> claimants = shift.Id == Guid.Empty
                ? []
                : (await ctx.ShiftClaims
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Open()
                    .Where(x => x.ShiftId == shift.Id && x.Kind == ShiftClaimKind.Pickup)
                    .Select(x => x.UserId)
                    .ToListAsync()).ToHashSet();

            Dictionary<string, StaffPosition> primaryPositions = (await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => userIds.Contains(x.UserId) && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                .ToListAsync())
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.First().StaffPosition!);

            List<SuggestionCandidate> candidates = users.Select(user =>
            {
                StaffPosition? primary = primaryPositions.GetValueOrDefault(user.Id);

                return new SuggestionCandidate(
                    user.Id,
                    RotaNames.For(user),
                    primary?.Id,
                    primary?.Name,
                    contracts.GetValueOrDefault(user.Id) ?? ResolvedContract.Empty,
                    shiftsByUser[user.Id].ToList(),
                    pendingTimeOff.GetValueOrDefault(user.Id),
                    claimants.Contains(user.Id),
                    ShiftClaimRules.WhyCantWork(calendars.GetValueOrDefault(user.Id, ShiftClaimRules.WorkerCalendar.Empty), shift, null, aboutViewer: false));
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
