using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

public class ContractRequirementService(IDbContextFactory<ApplicationDbContext> factory) : IContractRequirementService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<ContractRequirement?>> GetTierAsync(int companyId, Guid? positionId, string? userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ContractRequirement? row = await FindTierAsync(ctx, companyId, positionId, userId, track: false);

            return Result<ContractRequirement?>.Ok(row);
        }
        catch (Exception ex)
        {
            return Result<ContractRequirement?>.Fail($"Failed to retrieve contract requirements: {ex.Message}");
        }
    }

    public async Task<Result<ContractRequirement?>> SaveTierAsync(LocationScope scope, ContractRequirement row)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, row.CompanyId))
            {
                return Result<ContractRequirement?>.Fail("You can only edit contract requirements for your own company.");
            }

            if (row.StaffPositionId is not null && row.UserId is not null)
            {
                return Result<ContractRequirement?>.Fail("A contract requirement row belongs to either a position or a user, not both.");
            }

            string? validationError = Validate(row);

            if (validationError is not null)
            {
                return Result<ContractRequirement?>.Fail(validationError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (row.StaffPositionId is Guid positionId)
            {
                bool positionInCompany = await ctx.StaffPositions.AnyAsync(x => x.Id == positionId && x.CompanyId == row.CompanyId);

                if (!positionInCompany)
                {
                    return Result<ContractRequirement?>.Fail("That position does not belong to this company.");
                }
            }

            // An override row is validated against what it will actually combine with: a user
            // minimum of 50 h over an inherited company maximum of 40 h passes the single-row
            // check above but produces a contract that every week both under- and over-shoots.
            if (row.IsOverrideTier)
            {
                ResolvedContract parent = await ResolveParentAsync(ctx, row);

                decimal? effectiveMin = row.MinHoursPerWeek ?? parent.MinHoursPerWeek.Value;
                decimal? effectiveMax = row.MaxHoursPerWeek ?? parent.MaxHoursPerWeek.Value;

                if (effectiveMin is decimal min && effectiveMax is decimal max && min > max)
                {
                    string minSource = row.MinHoursPerWeek is null ? $"inherited from the {TierLabel(parent.MinHoursPerWeek.Source)}" : "set here";
                    string maxSource = row.MaxHoursPerWeek is null ? $"inherited from the {TierLabel(parent.MaxHoursPerWeek.Source)}" : "set here";

                    return Result<ContractRequirement?>.Fail(
                        $"Minimum hours per week ({min:0.##} h, {minSource}) cannot exceed the maximum ({max:0.##} h, {maxSource}).");
                }
            }

            ContractRequirement? existing = await FindTierAsync(ctx, row.CompanyId, row.StaffPositionId, row.UserId, track: true);

            if (row.IsOverrideTier && !row.HasAnyValue)
            {
                if (existing is not null)
                {
                    ctx.ContractRequirements.Remove(existing);
                    await ctx.SaveChangesAsync();
                }

                return Result<ContractRequirement?>.Ok(null);
            }

            if (existing is null)
            {
                existing = new ContractRequirement
                {
                    Id = Guid.NewGuid(),
                    CompanyId = row.CompanyId,
                    StaffPositionId = row.StaffPositionId,
                    UserId = row.UserId
                };

                ctx.ContractRequirements.Add(existing);
            }

            existing.MinShiftsPerWeek = row.MinShiftsPerWeek;
            existing.MinShiftLengthHours = row.MinShiftLengthHours;
            existing.MinHoursPerWeek = row.MinHoursPerWeek;
            existing.MaxHoursPerWeek = row.MaxHoursPerWeek;
            existing.UpdateDate = DateTime.UtcNow;
            existing.UpdatedByUserId = row.UpdatedByUserId;

            await ctx.SaveChangesAsync();

            return Result<ContractRequirement?>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<ContractRequirement?>.Fail($"Failed to save contract requirements: {ex.Message}");
        }
    }

    public async Task<Result<ResolvedContract>> ResolveAsync(int companyId, Guid? positionId, string? userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ContractRequirement> rows = await ctx.ContractRequirements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId
                    && ((x.StaffPositionId == null && x.UserId == null)
                        || (positionId != null && x.StaffPositionId == positionId)
                        || (userId != null && x.UserId == userId)))
                .ToListAsync();

            return Result<ResolvedContract>.Ok(Coalesce(rows, positionId, userId));
        }
        catch (Exception ex)
        {
            return Result<ResolvedContract>.Fail($"Failed to resolve contract requirements: {ex.Message}");
        }
    }

    public async Task<Result<ResolvedContract>> ResolveForUserAsync(string userId, int companyId)
    {
        Result<Dictionary<string, ResolvedContract>> result = await ResolveForUsersAsync([userId], companyId);

        if (!result.IsSuccess || result.Data is null)
        {
            return Result<ResolvedContract>.Fail(result.Error ?? "Failed to resolve contract requirements.");
        }

        return Result<ResolvedContract>.Ok(result.Data.TryGetValue(userId, out ResolvedContract? contract) ? contract : ResolvedContract.Empty);
    }

    public async Task<Result<Dictionary<string, ResolvedContract>>> ResolveForUsersAsync(IEnumerable<string> userIds, int companyId)
    {
        try
        {
            List<string> ids = userIds.Distinct().ToList();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // One primary per user is enforced by a partial unique index on UserPositions, but a
            // grouped read costs nothing and means a bad row could only ever affect one person.
            Dictionary<string, Guid> primaryPositionByUser = (await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => ids.Contains(x.UserId) && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                .Select(x => new { x.UserId, x.StaffPositionId })
                .ToListAsync())
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.First().StaffPositionId);

            List<Guid> positionIds = primaryPositionByUser.Values.Distinct().ToList();

            // One query for every row that could matter to any of these users, then the
            // per-user coalesce happens in memory - the rota builder resolves a whole location's
            // staff at once and shouldn't issue three queries per person.
            List<ContractRequirement> rows = await ctx.ContractRequirements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId
                    && ((x.StaffPositionId == null && x.UserId == null)
                        || (x.StaffPositionId != null && positionIds.Contains(x.StaffPositionId.Value))
                        || (x.UserId != null && ids.Contains(x.UserId))))
                .ToListAsync();

            Dictionary<string, ResolvedContract> result = new();

            foreach (string userId in ids)
            {
                Guid? positionId = primaryPositionByUser.TryGetValue(userId, out Guid p) ? p : null;
                result[userId] = Coalesce(rows, positionId, userId);
            }

            return Result<Dictionary<string, ResolvedContract>>.Ok(result);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, ResolvedContract>>.Fail($"Failed to resolve contract requirements: {ex.Message}");
        }
    }

    // The chain *below* an override row: company for a position row; the user's primary
    // position plus company for a user row.
    private static async Task<ResolvedContract> ResolveParentAsync(ApplicationDbContext ctx, ContractRequirement row)
    {
        Guid? positionId = row.StaffPositionId;

        if (row.UserId is string userId)
        {
            positionId = await ctx.UserPositions
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == row.CompanyId)
                .Select(x => (Guid?)x.StaffPositionId)
                .FirstOrDefaultAsync();
        }

        List<ContractRequirement> rows = await ctx.ContractRequirements
            .AsNoTracking()
            .Where(x => x.CompanyId == row.CompanyId
                && ((x.StaffPositionId == null && x.UserId == null)
                    || (positionId != null && x.StaffPositionId == positionId)))
            .ToListAsync();

        return Coalesce(rows, positionId, null);
    }

    private static string TierLabel(ContractTier tier) => tier switch
    {
        ContractTier.User => "user",
        ContractTier.Position => "position",
        ContractTier.Company => "company default",
        _ => "default"
    };

    private static async Task<ContractRequirement?> FindTierAsync(ApplicationDbContext ctx, int companyId, Guid? positionId, string? userId, bool track)
    {
        IQueryable<ContractRequirement> query = track ? ctx.ContractRequirements : ctx.ContractRequirements.AsNoTracking();

        query = query.TagWithCallSite().Where(x => x.CompanyId == companyId);

        query = userId is not null
            ? query.Where(x => x.UserId == userId)
            : positionId is not null
                ? query.Where(x => x.StaffPositionId == positionId)
                : query.Where(x => x.StaffPositionId == null && x.UserId == null);

        return await query.FirstOrDefaultAsync();
    }

    private static ResolvedContract Coalesce(List<ContractRequirement> rows, Guid? positionId, string? userId)
    {
        ContractRequirement? company = rows.FirstOrDefault(x => x.StaffPositionId is null && x.UserId is null);
        ContractRequirement? position = positionId is null ? null : rows.FirstOrDefault(x => x.StaffPositionId == positionId);
        ContractRequirement? user = userId is null ? null : rows.FirstOrDefault(x => x.UserId == userId);

        return new ResolvedContract(
            Pick(user?.MinShiftsPerWeek, position?.MinShiftsPerWeek, company?.MinShiftsPerWeek),
            Pick(user?.MinShiftLengthHours, position?.MinShiftLengthHours, company?.MinShiftLengthHours),
            Pick(user?.MinHoursPerWeek, position?.MinHoursPerWeek, company?.MinHoursPerWeek),
            Pick(user?.MaxHoursPerWeek, position?.MaxHoursPerWeek, company?.MaxHoursPerWeek));
    }

    private static ContractValue<T> Pick<T>(T? userValue, T? positionValue, T? companyValue) where T : struct
    {
        if (userValue is not null)
        {
            return new ContractValue<T>(userValue, ContractTier.User);
        }

        if (positionValue is not null)
        {
            return new ContractValue<T>(positionValue, ContractTier.Position);
        }

        if (companyValue is not null)
        {
            return new ContractValue<T>(companyValue, ContractTier.Company);
        }

        return ContractValue<T>.Unset;
    }

    private static string? Validate(ContractRequirement row)
    {
        if (row.MinShiftsPerWeek is < 0)
        {
            return "Minimum shifts per week cannot be negative.";
        }

        if (row.MinShiftLengthHours is < 0 or > 24)
        {
            return "Minimum shift length must be between 0 and 24 hours.";
        }

        if (row.MinHoursPerWeek is < 0 or > 168)
        {
            return "Minimum hours per week must be between 0 and 168.";
        }

        if (row.MaxHoursPerWeek is <= 0 or > 168)
        {
            return "Maximum hours per week must be between 1 and 168.";
        }

        if (row.MinHoursPerWeek is decimal min && row.MaxHoursPerWeek is decimal max && min > max)
        {
            return "Minimum hours per week cannot exceed the maximum.";
        }

        return null;
    }
}
