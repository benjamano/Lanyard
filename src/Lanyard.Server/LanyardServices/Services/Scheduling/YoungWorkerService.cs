using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class YoungWorkerService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<YoungWorkerService> logger) : IYoungWorkerService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<YoungWorkerService> _logger = logger;

    // Rows for some people at one company that still apply on `today` - what the rota reads.
    internal static IQueryable<YoungWorkerStatus> ForUsers(ApplicationDbContext ctx, IReadOnlyCollection<string> userIds, int companyId, DateOnly today) =>
        ctx.YoungWorkerStatuses
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId && userIds.Contains(x.UserId)
                && ((x.UnderEighteenUntil != null && x.UnderEighteenUntil > today) || (x.SchoolLeavingAgeUntil != null && x.SchoolLeavingAgeUntil > today)));

    // Someone can't be 18 any later than 18 years from now.
    public static string? Validate(DateOnly? underEighteenUntil, DateOnly? schoolLeavingAgeUntil, DateOnly today)
    {
        DateOnly latest = today.AddYears(18);

        if (underEighteenUntil is DateOnly adult && adult > latest)
        {
            return "Their 18th birthday can't be more than 18 years from today.";
        }

        if (schoolLeavingAgeUntil is DateOnly school && school > latest)
        {
            return "The school leaving date can't be more than 18 years from today.";
        }

        if (underEighteenUntil is DateOnly a && schoolLeavingAgeUntil is DateOnly s && s > a)
        {
            return "The school leaving date must be before their 18th birthday.";
        }

        return null;
    }

    public async Task<Result<YoungWorkerStatusView?>> GetAsync(LocationScope scope, string userId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (int? companyId, string? error) = await ResolveCompanyAsync(ctx, scope, userId, actingUserId, forChange: false);

            if (companyId is not int company)
            {
                return Result<YoungWorkerStatusView?>.Fail(error!);
            }

            YoungWorkerStatus? row = await ctx.YoungWorkerStatuses
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == company && x.UserId == userId);

            return Result<YoungWorkerStatusView?>.Ok(row is null ? null : await ToViewAsync(ctx, row));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load young worker dates for {UserId}", userId);
            return Result<YoungWorkerStatusView?>.Fail($"Failed to load young worker details: {ex.Message}");
        }
    }

    public async Task<Result<YoungWorkerStatusView?>> SaveAsync(LocationScope scope, string userId, DateOnly? underEighteenUntil, DateOnly? schoolLeavingAgeUntil, string actingUserId)
    {
        try
        {
            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

            if (Validate(underEighteenUntil, schoolLeavingAgeUntil, RotaTime.Today(nowUtc)) is string invalid)
            {
                return Result<YoungWorkerStatusView?>.Fail(invalid);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (int? companyId, string? error) = await ResolveCompanyAsync(ctx, scope, userId, actingUserId, forChange: true);

            if (companyId is not int company)
            {
                return Result<YoungWorkerStatusView?>.Fail(error!);
            }

            YoungWorkerStatus? row = await ctx.YoungWorkerStatuses.FirstOrDefaultAsync(x => x.CompanyId == company && x.UserId == userId);

            if (underEighteenUntil is null && schoolLeavingAgeUntil is null)
            {
                if (row is not null)
                {
                    ctx.YoungWorkerStatuses.Remove(row);
                    await ctx.SaveChangesAsync();
                }

                // The dates themselves are never logged.
                _logger.LogInformation("Young worker dates cleared for {UserId} by {ActingUserId}", userId, actingUserId);
                return Result<YoungWorkerStatusView?>.Ok(null);
            }

            if (row is null)
            {
                row = new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = company, UserId = userId };
                ctx.YoungWorkerStatuses.Add(row);
            }

            row.UnderEighteenUntil = underEighteenUntil;
            row.SchoolLeavingAgeUntil = schoolLeavingAgeUntil;
            row.UpdateDate = nowUtc;
            row.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Young worker dates set for {UserId} by {ActingUserId}", userId, actingUserId);

            return Result<YoungWorkerStatusView?>.Ok(await ToViewAsync(ctx, row));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save young worker dates for {UserId}", userId);
            return Result<YoungWorkerStatusView?>.Fail($"Failed to save young worker details: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<string, YoungWorkerStatus>>> GetForCompanyAsync(LocationScope scope, DateOnly today)
    {
        try
        {
            if (scope.CompanyId is not int companyId || !SchedulingAccess.CanManageCompany(scope, companyId))
            {
                return Result<Dictionary<string, YoungWorkerStatus>>.Fail("Only Managers and Admins can see who is a young worker.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dictionary<string, YoungWorkerStatus> rows = await ctx.YoungWorkerStatuses
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId
                    && ((x.UnderEighteenUntil != null && x.UnderEighteenUntil > today) || (x.SchoolLeavingAgeUntil != null && x.SchoolLeavingAgeUntil > today)))
                .ToDictionaryAsync(x => x.UserId);

            return Result<Dictionary<string, YoungWorkerStatus>>.Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load young workers for company {CompanyId}", scope.CompanyId);
            return Result<Dictionary<string, YoungWorkerStatus>>.Fail($"Failed to load young workers: {ex.Message}");
        }
    }

    public async Task<Result<List<YoungWorkerBreach>>> CheckShiftAsync(LocationScope scope, Shift draft)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, draft.LocationId))
            {
                return Result<List<YoungWorkerBreach>>.Fail("You can only check shifts for your own location.");
            }

            if (draft.UserId is not string userId || draft.EndUtc <= draft.StartUtc)
            {
                return Result<List<YoungWorkerBreach>>.Ok([]);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<List<YoungWorkerBreach>>.Ok(await ShiftClaimRules.YoungWorkerBreachesAsync(ctx, userId, draft));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check young worker limits for {UserId}", draft.UserId);
            return Result<List<YoungWorkerBreach>>.Fail($"Failed to check the young worker limits: {ex.Message}");
        }
    }

    public async Task<Result<bool>> IsYoungWorkerAsync(string userId, int companyId, DateOnly today)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<bool>.Ok(await ForUsers(ctx, [userId], companyId, today).AnyAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check young worker status for {UserId}", userId);
            return Result<bool>.Fail($"Failed to check young worker status: {ex.Message}");
        }
    }

    // Deletes rows whose dates have all passed: once someone is 18 and past school leaving age the
    // row only says when their birthday is, which there's no reason to keep. Run by
    // YoungWorkerCleanupHostedService across every company (system context, no tenant filter).
    public static async Task<int> RemoveExpiredAsync(ApplicationDbContext ctx, DateOnly today, CancellationToken cancellationToken = default)
    {
        List<YoungWorkerStatus> expired = await ctx.YoungWorkerStatuses
            .IgnoreQueryFilters()
            .TagWithCallSite()
            .Where(x => (x.UnderEighteenUntil == null || x.UnderEighteenUntil <= today)
                && (x.SchoolLeavingAgeUntil == null || x.SchoolLeavingAgeUntil <= today))
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        ctx.YoungWorkerStatuses.RemoveRange(expired);
        await ctx.SaveChangesAsync(cancellationToken);

        return expired.Count;
    }

    private static async Task<YoungWorkerStatusView> ToViewAsync(ApplicationDbContext ctx, YoungWorkerStatus row)
    {
        string? updatedBy = null;

        if (row.UpdatedByUserId is string editorId)
        {
            UserProfile? editor = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == editorId);

            updatedBy = editor is null ? null : RotaNames.For(editor);
        }

        return new YoungWorkerStatusView(row.UnderEighteenUntil, row.SchoolLeavingAgeUntil, row.UpdateDate, updatedBy);
    }

    // Which company the person's dates belong to, or why the caller can't see or change them.
    // Managers and Admins only, and only for someone they manage; nobody changes their own (a
    // young manager mustn't be able to switch their own limits off); only an Admin may change an
    // Admin's.
    private static async Task<(int? CompanyId, string? Error)> ResolveCompanyAsync(ApplicationDbContext ctx, LocationScope scope, string userId, string actingUserId, bool forChange)
    {
        if (!scope.IsAdmin && !scope.IsManager)
        {
            return (null, "Only Managers and Admins can see young worker details.");
        }

        if (forChange && userId == actingUserId)
        {
            return (null, "You can't change your own young worker details. Ask another manager or an Admin.");
        }

        if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
        {
            return (null, "You can only see young worker details for people you manage.");
        }

        if (forChange && !scope.IsAdmin && await IsAdminAsync(ctx, userId))
        {
            return (null, "Only an Admin can change young worker details for an Admin.");
        }

        List<int> userCompanies = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.Location!.IsActive)
            .Select(x => x.Location!.CompanyId)
            .Distinct()
            .ToListAsync();

        if (scope.CompanyId is int managed && userCompanies.Contains(managed))
        {
            return (managed, null);
        }

        return scope.IsAdmin && userCompanies.Count == 1
            ? (userCompanies[0], null)
            : (null, "This person isn't a member of exactly one company, so their young worker details can't be edited here.");
    }

    private static Task<bool> IsAdminAsync(ApplicationDbContext ctx, string userId) =>
        (from userRole in ctx.UserRoles
         join role in ctx.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && role.Name == "Admin"
         select userRole.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync();
}
