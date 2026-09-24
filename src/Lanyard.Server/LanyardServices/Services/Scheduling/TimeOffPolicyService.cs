using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class TimeOffPolicyService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<TimeOffPolicyService> logger) : ITimeOffPolicyService
{
    // A year of hours - anything bigger is a typo, and "unlimited" is its own switch.
    private const decimal MaxAllowanceHours = 8784m;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<TimeOffPolicyService> _logger = logger;

    public async Task<Result<List<TimeOffType>>> GetTypesAsync(int companyId, bool includeInactive = false)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TimeOffType> types = await ctx.TimeOffTypes
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            if (types.Count == 0)
            {
                types = await SeedDefaultTypesAsync(ctx, companyId);
            }

            return Result<List<TimeOffType>>.Ok(includeInactive ? types : types.Where(x => x.IsActive).ToList());
        }
        catch (Exception ex)
        {
            return Result<List<TimeOffType>>.Fail($"Failed to retrieve time-off types: {ex.Message}");
        }
    }

    public async Task<Result<TimeOffType>> SaveTypeAsync(LocationScope scope, TimeOffType type)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, type.CompanyId))
            {
                return Result<TimeOffType>.Fail("You can only edit time-off types for your own company.");
            }

            string name = type.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                return Result<TimeOffType>.Fail("Give the type a name.");
            }

            if (name.Length > 60)
            {
                return Result<TimeOffType>.Fail("Keep the name to 60 characters or fewer.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string lowered = name.ToLower();

            bool duplicate = await ctx.TimeOffTypes.AnyAsync(x =>
                x.CompanyId == type.CompanyId && x.Id != type.Id && x.Name.ToLower() == lowered);

            if (duplicate)
            {
                return Result<TimeOffType>.Fail($"There's already a time-off type called \"{name}\".");
            }

            TimeOffType? existing = null;

            if (type.Id != Guid.Empty)
            {
                existing = await ctx.TimeOffTypes.FirstOrDefaultAsync(x => x.Id == type.Id);

                if (existing is null || existing.CompanyId != type.CompanyId)
                {
                    return Result<TimeOffType>.Fail("That time-off type no longer exists.");
                }
            }

            if (existing is null)
            {
                int nextSort = await ctx.TimeOffTypes.Where(x => x.CompanyId == type.CompanyId).Select(x => (int?)x.SortOrder).MaxAsync() ?? -1;

                existing = new TimeOffType
                {
                    Id = Guid.NewGuid(),
                    CompanyId = type.CompanyId,
                    Name = name,
                    SortOrder = nextSort + 1,
                    IsActive = true
                };

                ctx.TimeOffTypes.Add(existing);
            }

            existing.Name = name;
            existing.Description = string.IsNullOrWhiteSpace(type.Description) ? null : type.Description.Trim();
            existing.IsPaid = type.IsPaid;
            existing.DeductsFromAllowance = type.DeductsFromAllowance;
            existing.IsActive = type.IsActive;

            await ctx.SaveChangesAsync();

            return Result<TimeOffType>.Ok(existing);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning("Saving time-off type {Name} for company {CompanyId} hit a database constraint: {Error}", type.Name, type.CompanyId, ex.InnerException?.Message ?? ex.Message);
            return Result<TimeOffType>.Fail($"There's already a time-off type called \"{type.Name?.Trim()}\".");
        }
        catch (Exception ex)
        {
            return Result<TimeOffType>.Fail($"Failed to save the time-off type: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateTypeAsync(LocationScope scope, Guid typeId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeOffType? type = await ctx.TimeOffTypes.FirstOrDefaultAsync(x => x.Id == typeId);

            if (type is null)
            {
                return Result<bool>.Fail("That time-off type no longer exists.");
            }

            if (!SchedulingAccess.CanManageCompany(scope, type.CompanyId))
            {
                return Result<bool>.Fail("You can only edit time-off types for your own company.");
            }

            type.IsActive = false;
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to archive the time-off type: {ex.Message}");
        }
    }

    public async Task<Result<List<TimeOffAllowance>>> GetTierAsync(int companyId, Guid? positionId, string? userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TimeOffAllowance> rows = await TierQuery(ctx.TimeOffAllowances.AsNoTracking().TagWithCallSite(), companyId, positionId, userId)
                .ToListAsync();

            return Result<List<TimeOffAllowance>>.Ok(rows);
        }
        catch (Exception ex)
        {
            return Result<List<TimeOffAllowance>>.Fail($"Failed to retrieve allowances: {ex.Message}");
        }
    }

    public async Task<Result<TimeOffAllowance>> SaveAllowanceAsync(LocationScope scope, TimeOffAllowance row)
    {
        try
        {
            if (row.StaffPositionId is not null && row.UserId is not null)
            {
                return Result<TimeOffAllowance>.Fail("An allowance belongs to either a position or a person, not both.");
            }

            if (!row.IsUnlimited && (row.AllowanceHours < 0 || row.AllowanceHours > MaxAllowanceHours))
            {
                return Result<TimeOffAllowance>.Fail("An allowance must be between 0 and 8,784 hours (a whole year).");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, row.CompanyId, row.TimeOffTypeId, row.StaffPositionId, row.UserId);

            if (accessError is not null)
            {
                return Result<TimeOffAllowance>.Fail(accessError);
            }

            TimeOffAllowance? existing = await TierQuery(ctx.TimeOffAllowances, row.CompanyId, row.StaffPositionId, row.UserId)
                .FirstOrDefaultAsync(x => x.TimeOffTypeId == row.TimeOffTypeId);

            if (existing is null)
            {
                existing = new TimeOffAllowance
                {
                    Id = Guid.NewGuid(),
                    CompanyId = row.CompanyId,
                    TimeOffTypeId = row.TimeOffTypeId,
                    StaffPositionId = row.StaffPositionId,
                    UserId = row.UserId
                };

                ctx.TimeOffAllowances.Add(existing);
            }

            existing.IsUnlimited = row.IsUnlimited;
            existing.AllowanceHours = row.IsUnlimited ? 0m : Math.Round(row.AllowanceHours, 2);
            existing.UpdateDate = DateTime.UtcNow;
            existing.UpdatedByUserId = row.UpdatedByUserId;

            await ctx.SaveChangesAsync();

            return Result<TimeOffAllowance>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<TimeOffAllowance>.Fail($"Failed to save the allowance: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveAllowanceAsync(LocationScope scope, int companyId, Guid typeId, Guid? positionId, string? userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, companyId, typeId, positionId, userId);

            if (accessError is not null)
            {
                return Result<bool>.Fail(accessError);
            }

            TimeOffAllowance? existing = await TierQuery(ctx.TimeOffAllowances, companyId, positionId, userId)
                .FirstOrDefaultAsync(x => x.TimeOffTypeId == typeId);

            if (existing is not null)
            {
                ctx.TimeOffAllowances.Remove(existing);
                await ctx.SaveChangesAsync();
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the allowance: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, ResolvedAllowance>>> ResolveAsync(int companyId, Guid? positionId, string? userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<Dictionary<Guid, ResolvedAllowance>>.Ok(await ResolveCoreAsync(ctx, companyId, positionId, userId));
        }
        catch (Exception ex)
        {
            return Result<Dictionary<Guid, ResolvedAllowance>>.Fail($"Failed to work out allowances: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, ResolvedAllowance>>> ResolveForUserAsync(string userId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Guid? primaryPositionId = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                .Select(x => (Guid?)x.StaffPositionId)
                .FirstOrDefaultAsync();

            return Result<Dictionary<Guid, ResolvedAllowance>>.Ok(await ResolveCoreAsync(ctx, companyId, primaryPositionId, userId));
        }
        catch (Exception ex)
        {
            return Result<Dictionary<Guid, ResolvedAllowance>>.Fail($"Failed to work out allowances: {ex.Message}");
        }
    }

    // Shared by both resolve paths. One query for every row that could apply, coalesced per type
    // in memory: user beats position beats company, and a missing row falls through.
    internal static async Task<Dictionary<Guid, ResolvedAllowance>> ResolveCoreAsync(ApplicationDbContext ctx, int companyId, Guid? positionId, string? userId)
    {
        List<Guid> typeIds = await ctx.TimeOffTypes
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.IsActive)
            .Select(x => x.Id)
            .ToListAsync();

        List<TimeOffAllowance> rows = await ctx.TimeOffAllowances
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId
                && ((x.StaffPositionId == null && x.UserId == null)
                    || (positionId != null && x.StaffPositionId == positionId)
                    || (userId != null && x.UserId == userId)))
            .ToListAsync();

        Dictionary<Guid, ResolvedAllowance> result = [];

        foreach (Guid typeId in typeIds)
        {
            TimeOffAllowance? user = userId is null ? null : rows.FirstOrDefault(x => x.TimeOffTypeId == typeId && x.UserId == userId);
            TimeOffAllowance? position = positionId is null ? null : rows.FirstOrDefault(x => x.TimeOffTypeId == typeId && x.StaffPositionId == positionId);
            TimeOffAllowance? company = rows.FirstOrDefault(x => x.TimeOffTypeId == typeId && x.StaffPositionId == null && x.UserId == null);

            result[typeId] = (user, position, company) switch
            {
                ({ } u, _, _) => new ResolvedAllowance(typeId, u.IsUnlimited, u.AllowanceHours, ContractTier.User),
                (null, { } p, _) => new ResolvedAllowance(typeId, p.IsUnlimited, p.AllowanceHours, ContractTier.Position),
                (null, null, { } c) => new ResolvedAllowance(typeId, c.IsUnlimited, c.AllowanceHours, ContractTier.Company),
                _ => ResolvedAllowance.None(typeId)
            };
        }

        return result;
    }

    private static IQueryable<TimeOffAllowance> TierQuery(IQueryable<TimeOffAllowance> query, int companyId, Guid? positionId, string? userId)
    {
        query = query.Where(x => x.CompanyId == companyId);

        return userId is not null
            ? query.Where(x => x.UserId == userId)
            : positionId is not null
                ? query.Where(x => x.StaffPositionId == positionId)
                : query.Where(x => x.StaffPositionId == null && x.UserId == null);
    }

    private static async Task<string?> CheckAccessAsync(ApplicationDbContext ctx, LocationScope scope, int companyId, Guid typeId, Guid? positionId, string? userId)
    {
        if (!SchedulingAccess.CanManageCompany(scope, companyId))
        {
            return "You can only edit allowances for your own company.";
        }

        if (userId is not null && !await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
        {
            return "You can only edit allowances for people at your own company.";
        }

        bool typeInCompany = await ctx.TimeOffTypes.AnyAsync(x => x.Id == typeId && x.CompanyId == companyId);

        if (!typeInCompany)
        {
            return "That time-off type doesn't belong to this company.";
        }

        if (positionId is Guid position && !await ctx.StaffPositions.AnyAsync(x => x.Id == position && x.CompanyId == companyId))
        {
            return "That position doesn't belong to this company.";
        }

        return null;
    }

    // The starting set every company gets. Allowance amounts are deliberately not seeded: how
    // much holiday people get is company policy (and differs by position), so it's set on the
    // Time Off Types page rather than guessed here.
    private async Task<List<TimeOffType>> SeedDefaultTypesAsync(ApplicationDbContext ctx, int companyId)
    {
        List<TimeOffType> defaults =
        [
            new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Paid holiday", IsPaid = true, DeductsFromAllowance = true, SortOrder = 0 },
            new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Unpaid leave", IsPaid = false, DeductsFromAllowance = true, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Sickness", IsPaid = false, DeductsFromAllowance = false, SortOrder = 2,
                Description = "Recorded for the rota and records; never taken from anyone's allowance." }
        ];

        ctx.TimeOffTypes.AddRange(defaults);

        try
        {
            await ctx.SaveChangesAsync();
            _logger.LogInformation("Created the default time-off types for company {CompanyId}", companyId);
            return defaults;
        }
        catch (DbUpdateException)
        {
            // Two first reads racing: the other one's rows won the unique index. Use those.
            ctx.ChangeTracker.Clear();

            return await ctx.TimeOffTypes
                .AsNoTracking()
                .Where(x => x.CompanyId == companyId)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();
        }
    }
}
