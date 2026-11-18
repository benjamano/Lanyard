using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Parties;

public class PartyStaffingService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<PartyStaffingService> logger) : IPartyStaffingService
{
    private const string NoAccess = "You can only change party settings for your own location.";

    public const int MaxStaff = 20;
    public const int MaxTypeLength = 100;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<PartyStaffingService> _logger = logger;

    // Active rules for one location with positions - what Auto-build reads.
    internal static IQueryable<PartyStaffingRule> ForLocation(ApplicationDbContext ctx, int locationId) =>
        ctx.PartyStaffingRules
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.StaffPosition)
            .Where(x => x.LocationId == locationId && x.IsActive);

    public async Task<Result<List<PartyStaffingRule>>> GetRulesAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<PartyStaffingRule>>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<PartyStaffingRule> rules = await ForLocation(ctx, locationId).ToListAsync();

            return Result<List<PartyStaffingRule>>.Ok(rules
                .OrderBy(x => x.HireKind is not null)
                .ThenBy(x => x.HireKind)
                .ThenBy(x => x.PartyType is null)
                .ThenBy(x => x.PartyType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.StaffPosition?.SortOrder ?? int.MaxValue)
                .ThenBy(x => x.StaffPosition?.Name)
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load party staffing rules for {LocationId}", locationId);
            return Result<List<PartyStaffingRule>>.Fail($"Failed to load party staffing: {ex.Message}");
        }
    }

    public async Task<Result<PartyStaffingRule>> SaveRuleAsync(LocationScope scope, PartyStaffingRule rule, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, rule.LocationId))
            {
                return Result<PartyStaffingRule>.Fail(NoAccess);
            }

            // A private hire's line has no party type; a kids' party's line has no hire kind.
            string? partyType = rule.HireKind is null ? PartyStaffingRule.Normalise(rule.PartyType) : null;

            if (partyType is { Length: > MaxTypeLength })
            {
                return Result<PartyStaffingRule>.Fail($"Keep the party type to {MaxTypeLength} characters or fewer.");
            }

            if (rule.StaffCount is < 1 or > MaxStaff)
            {
                return Result<PartyStaffingRule>.Fail($"A party needs between 1 and {MaxStaff} staff of a position.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == rule.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<PartyStaffingRule>.Fail("Location not found.");
            }

            PartyStaffingRule? existing = null;

            if (rule.Id != Guid.Empty)
            {
                existing = await ctx.PartyStaffingRules.FirstOrDefaultAsync(x => x.Id == rule.Id && x.LocationId == rule.LocationId && x.IsActive);

                if (existing is null)
                {
                    return Result<PartyStaffingRule>.Fail("That party staffing rule wasn't found.");
                }
            }

            StaffPosition? position = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == rule.StaffPositionId && x.CompanyId == location.CompanyId);

            if (position is null || (!position.IsActive && existing?.StaffPositionId != position.Id))
            {
                return Result<PartyStaffingRule>.Fail("That position isn't available any more. Choose another.");
            }

            List<PartyStaffingRule> others = await ctx.PartyStaffingRules
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == rule.LocationId && x.IsActive && x.StaffPositionId == rule.StaffPositionId && x.Id != rule.Id)
                .ToListAsync();

            if (others.Any(x => x.HireKind == rule.HireKind && (rule.HireKind is not null || x.Matches(partyType))))
            {
                string what = rule.HireKind is PrivateHireKind kind ? $"{kind.Label()} private hires"
                    : partyType is null ? "Other parties" : $"{partyType} parties";

                return Result<PartyStaffingRule>.Fail($"{what} already have a line for {position.Name}. Change that one instead.");
            }

            PartyStaffingRule target = existing ?? new PartyStaffingRule
            {
                Id = Guid.NewGuid(),
                CompanyId = location.CompanyId,
                LocationId = location.Id,
                StaffPositionId = rule.StaffPositionId
            };

            if (existing is null)
            {
                ctx.PartyStaffingRules.Add(target);
            }

            target.PartyType = partyType;
            target.HireKind = rule.HireKind;
            target.StaffPositionId = rule.StaffPositionId;
            target.StaffCount = rule.StaffCount;
            target.UpdateDate = DateTime.UtcNow;
            target.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Party staffing rule {RuleId} saved for {LocationId} by {ActingUserId}", target.Id, target.LocationId, actingUserId);

            target.StaffPosition = position;
            return Result<PartyStaffingRule>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save party staffing rule for {LocationId}", rule.LocationId);
            return Result<PartyStaffingRule>.Fail($"Failed to save party staffing: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveRuleAsync(LocationScope scope, Guid ruleId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyStaffingRule? rule = await ctx.PartyStaffingRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.IsActive);

            if (rule is null || !SchedulingAccess.CanManageLocation(scope, rule.LocationId))
            {
                return Result<bool>.Fail("That party staffing rule wasn't found.");
            }

            rule.IsActive = false;
            rule.UpdateDate = DateTime.UtcNow;
            rule.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Party staffing rule {RuleId} removed by {ActingUserId}", ruleId, actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove party staffing rule {RuleId}", ruleId);
            return Result<bool>.Fail($"Failed to remove party staffing: {ex.Message}");
        }
    }
}
