using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

public class StaffPositionService(IDbContextFactory<ApplicationDbContext> factory) : IStaffPositionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    // Company catalogs are company policy, so a manager may edit them for any company they
    // logged in under; admins see everything. Same rule as onboarding/staff document types.
    private static bool CanManageCompany(LocationScope scope, int companyId) =>
        scope.IsAdmin || scope.CompanyId == companyId;

    public async Task<Result<List<StaffPosition>>> GetPositionsAsync(int companyId, bool includeInactive = false)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && (includeInactive || x.IsActive))
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            return Result<List<StaffPosition>>.Ok(positions);
        }
        catch (Exception ex)
        {
            return Result<List<StaffPosition>>.Fail($"Failed to retrieve positions: {ex.Message}");
        }
    }

    public async Task<Result<StaffPosition>> SavePositionAsync(LocationScope scope, StaffPosition position)
    {
        try
        {
            if (!CanManageCompany(scope, position.CompanyId))
            {
                return Result<StaffPosition>.Fail("You can only manage positions for your own company.");
            }

            string name = position.Name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                return Result<StaffPosition>.Fail("A position name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Pre-checked here because EF InMemory (tests) doesn't enforce the unique index; the
            // index remains the guard against a race between two managers.
            bool duplicate = await ctx.StaffPositions
                .AnyAsync(x => x.CompanyId == position.CompanyId && x.Id != position.Id && x.Name.ToLower() == name.ToLower());

            if (duplicate)
            {
                return Result<StaffPosition>.Fail($"A position called \"{name}\" already exists for this company.");
            }

            if (position.Id == Guid.Empty)
            {
                position.Id = Guid.NewGuid();
                position.Name = name;
                position.IsActive = true;
                ctx.StaffPositions.Add(position);
            }
            else
            {
                StaffPosition? existing = await ctx.StaffPositions
                    .FirstOrDefaultAsync(x => x.Id == position.Id && x.CompanyId == position.CompanyId);

                if (existing is null)
                {
                    return Result<StaffPosition>.Fail("Position not found.");
                }

                existing.Name = name;
                existing.Description = position.Description;
                existing.ColorIndex = position.ColorIndex;
                existing.SortOrder = position.SortOrder;
                existing.IsActive = position.IsActive;
            }

            await ctx.SaveChangesAsync();

            return Result<StaffPosition>.Ok(position);
        }
        catch (DbUpdateException)
        {
            return Result<StaffPosition>.Fail("A position with that name already exists for this company.");
        }
        catch (Exception ex)
        {
            return Result<StaffPosition>.Fail($"Failed to save position: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivatePositionAsync(LocationScope scope, Guid positionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffPosition? position = await ctx.StaffPositions.FirstOrDefaultAsync(x => x.Id == positionId);

            if (position is null)
            {
                return Result<bool>.Fail("Position not found.");
            }

            if (!CanManageCompany(scope, position.CompanyId))
            {
                return Result<bool>.Fail("You can only manage positions for your own company.");
            }

            position.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate position: {ex.Message}");
        }
    }

    public async Task<Result<List<UserPosition>>> GetUserPositionsAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<UserPosition> positions = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => x.UserId == userId && x.StaffPosition!.IsActive)
                .OrderByDescending(x => x.IsPrimary)
                .ThenBy(x => x.StaffPosition!.SortOrder)
                .ToListAsync();

            return Result<List<UserPosition>>.Ok(positions);
        }
        catch (Exception ex)
        {
            return Result<List<UserPosition>>.Fail($"Failed to retrieve the user's positions: {ex.Message}");
        }
    }

    public async Task<Result<List<UserPosition>>> SetUserPositionsAsync(LocationScope scope, string userId, List<Guid> positionIds, Guid? primaryPositionId)
    {
        try
        {
            List<Guid> distinctIds = positionIds.Distinct().ToList();

            if (distinctIds.Count == 0 && primaryPositionId is not null)
            {
                return Result<List<UserPosition>>.Fail("The primary position must be one of the selected positions.");
            }

            if (primaryPositionId is Guid primary && !distinctIds.Contains(primary))
            {
                return Result<List<UserPosition>>.Fail("The primary position must be one of the selected positions.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .Where(x => distinctIds.Contains(x.Id) && x.IsActive)
                .ToListAsync();

            if (positions.Count != distinctIds.Count)
            {
                return Result<List<UserPosition>>.Fail("One or more of the selected positions no longer exists.");
            }

            // All positions in one set must share a company, and it must be one the caller may
            // manage - a manager can't hand someone a position from a company they don't belong to.
            List<int> companyIds = positions.Select(x => x.CompanyId).Distinct().ToList();

            if (companyIds.Count > 1)
            {
                return Result<List<UserPosition>>.Fail("Positions from different companies cannot be combined.");
            }

            if (companyIds.Count == 1 && !CanManageCompany(scope, companyIds[0]))
            {
                return Result<List<UserPosition>>.Fail("You can only assign positions from your own company.");
            }

            List<UserPosition> existing = await ctx.UserPositions
                .Where(x => x.UserId == userId)
                .ToListAsync();

            Guid? effectivePrimary = primaryPositionId ?? distinctIds.FirstOrDefault();

            foreach (UserPosition row in existing.Where(x => !distinctIds.Contains(x.StaffPositionId)).ToList())
            {
                ctx.UserPositions.Remove(row);
                existing.Remove(row);
            }

            foreach (Guid positionId in distinctIds)
            {
                UserPosition? row = existing.FirstOrDefault(x => x.StaffPositionId == positionId);

                if (row is null)
                {
                    row = new UserPosition
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        StaffPositionId = positionId,
                        CreateDate = DateTime.UtcNow
                    };

                    ctx.UserPositions.Add(row);
                    existing.Add(row);
                }

                row.IsPrimary = positionId == effectivePrimary;
            }

            await ctx.SaveChangesAsync();

            return await GetUserPositionsAsync(userId);
        }
        catch (Exception ex)
        {
            return Result<List<UserPosition>>.Fail($"Failed to save the user's positions: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<string, UserPosition?>>> GetPrimaryPositionsForUsersAsync(IEnumerable<string> userIds)
    {
        try
        {
            List<string> ids = userIds.Distinct().ToList();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<UserPosition> primaries = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => ids.Contains(x.UserId) && x.IsPrimary && x.StaffPosition!.IsActive)
                .ToListAsync();

            Dictionary<string, UserPosition?> result = ids.ToDictionary(id => id, _ => (UserPosition?)null);

            foreach (UserPosition primary in primaries)
            {
                result[primary.UserId] = primary;
            }

            return Result<Dictionary<string, UserPosition?>>.Ok(result);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<string, UserPosition?>>.Fail($"Failed to retrieve primary positions: {ex.Message}");
        }
    }
}
