using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class StaffAvailabilityService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<StaffAvailabilityService> logger) : IStaffAvailabilityService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<StaffAvailabilityService> _logger = logger;

    // Active windows for some people at one company - what the rota and suggestions read.
    internal static IQueryable<StaffAvailability> ForUsers(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, int companyId) =>
        ctx.StaffAvailabilities
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && x.CompanyId == companyId && userIds.Contains(x.UserId));

    public async Task<Result<List<StaffAvailability>>> GetAsync(LocationScope scope, string userId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (int? companyId, string? error) = await ResolveCompanyAsync(ctx, scope, userId, actingUserId);

            if (companyId is not int company)
            {
                return Result<List<StaffAvailability>>.Fail(error!);
            }

            List<StaffAvailability> rows = await ForUsers(ctx, [userId], company).ToListAsync();

            return Result<List<StaffAvailability>>.Ok(Ordered(rows));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load availability for {UserId}", userId);
            return Result<List<StaffAvailability>>.Fail($"Failed to load availability: {ex.Message}");
        }
    }

    public async Task<Result<StaffAvailability>> SaveAsync(LocationScope scope, StaffAvailability row, string actingUserId)
    {
        try
        {
            if (StaffAvailabilityRules.Validate(row) is string invalid)
            {
                return Result<StaffAvailability>.Fail(invalid);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (int? companyId, string? error) = await ResolveCompanyAsync(ctx, scope, row.UserId, actingUserId);

            if (companyId is not int company)
            {
                return Result<StaffAvailability>.Fail(error!);
            }

            StaffAvailability target;

            if (row.Id == Guid.Empty)
            {
                target = new StaffAvailability { Id = Guid.NewGuid(), CompanyId = company, UserId = row.UserId };
                ctx.StaffAvailabilities.Add(target);
            }
            else
            {
                StaffAvailability? existing = await ctx.StaffAvailabilities
                    .FirstOrDefaultAsync(x => x.Id == row.Id && x.UserId == row.UserId && x.CompanyId == company && x.IsActive);

                if (existing is null)
                {
                    return Result<StaffAvailability>.Fail("That availability wasn't found.");
                }

                target = existing;
            }

            target.DayOfWeek = row.DayOfWeek;
            target.StartTime = row.StartTime;
            target.EndTime = row.EndTime;
            target.Kind = row.Kind;
            target.Notes = string.IsNullOrWhiteSpace(row.Notes) ? null : row.Notes.Trim();
            target.EffectiveFrom = row.EffectiveFrom;
            target.EffectiveTo = row.EffectiveTo;
            target.UpdateDate = DateTime.UtcNow;
            target.UpdatedByUserId = actingUserId == row.UserId ? null : actingUserId;

            List<StaffAvailability> others = await ForUsers(ctx, [row.UserId], company)
                .Where(x => x.Id != target.Id)
                .ToListAsync();

            if (StaffAvailabilityRules.FindOverlap([.. others, target]) is string overlap)
            {
                return Result<StaffAvailability>.Fail(overlap);
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Availability {AvailabilityId} saved for {UserId} by {ActingUserId}", target.Id, row.UserId, actingUserId);

            return Result<StaffAvailability>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save availability for {UserId}", row.UserId);
            return Result<StaffAvailability>.Fail($"Failed to save availability: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveAsync(LocationScope scope, Guid id, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffAvailability? row = await ctx.StaffAvailabilities.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);

            if (row is null)
            {
                return Result<bool>.Fail("That availability wasn't found.");
            }

            (int? companyId, string? error) = await ResolveCompanyAsync(ctx, scope, row.UserId, actingUserId);

            if (companyId != row.CompanyId)
            {
                return Result<bool>.Fail(error ?? "That availability wasn't found.");
            }

            row.IsActive = false;
            row.UpdateDate = DateTime.UtcNow;
            row.UpdatedByUserId = actingUserId == row.UserId ? null : actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Availability {AvailabilityId} removed for {UserId} by {ActingUserId}", id, row.UserId, actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove availability {AvailabilityId}", id);
            return Result<bool>.Fail($"Failed to remove availability: {ex.Message}");
        }
    }

    // Which company's availability the caller is reading or changing, or why they can't. Someone
    // editing their own uses the company they signed in under; a Manager can edit people at a
    // location in their company; an Admin anyone, in the person's one company.
    private static async Task<(int? CompanyId, string? Error)> ResolveCompanyAsync(ApplicationDbContext ctx, LocationScope scope, string userId, string actingUserId)
    {
        List<int> userCompanies = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.Location!.IsActive)
            .Select(x => x.Location!.CompanyId)
            .Distinct()
            .ToListAsync();

        if (userId == actingUserId)
        {
            return scope.CompanyId is int own && userCompanies.Contains(own)
                ? (own, null)
                : (null, "Sign in at one of your locations to change your availability.");
        }

        if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
        {
            return (null, "You can only change availability for people you manage.");
        }

        if (scope.CompanyId is int managed && userCompanies.Contains(managed))
        {
            return (managed, null);
        }

        return scope.IsAdmin && userCompanies.Count == 1
            ? (userCompanies[0], null)
            : (null, "This person isn't a member of exactly one company, so their availability can't be edited here.");
    }

    // Monday first, then by start time - the order the week reads in.
    private static List<StaffAvailability> Ordered(IEnumerable<StaffAvailability> rows) =>
        rows.OrderBy(x => ((int)x.DayOfWeek + 6) % 7).ThenBy(x => x.StartTime).ToList();
}
