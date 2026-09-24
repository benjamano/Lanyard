using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lanyard.Application.Services.Scheduling;

public class StaffPositionService(IDbContextFactory<ApplicationDbContext> factory) : IStaffPositionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

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
            if (!SchedulingAccess.CanManageCompany(scope, position.CompanyId))
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
            StaffPosition? sameName = await ctx.StaffPositions
                .FirstOrDefaultAsync(x => x.CompanyId == position.CompanyId && x.Id != position.Id && x.Name.ToLower() == name.ToLower());

            if (sameName is not null && sameName.IsActive)
            {
                return Result<StaffPosition>.Fail($"A position called \"{name}\" already exists for this company.");
            }

            StaffPosition saved;

            if (position.Id == Guid.Empty)
            {
                // Deactivation is soft, and the (CompanyId, Name) index covers inactive rows too,
                // so "create X" where an inactive X exists brings the old row back rather than
                // reserving the name forever. Assignments it had before deactivation reappear.
                if (sameName is not null)
                {
                    sameName.Name = name;
                    sameName.Description = position.Description;
                    sameName.ColorIndex = position.ColorIndex;
                    sameName.SortOrder = position.SortOrder;
                    sameName.IsActive = true;
                    saved = sameName;
                }
                else
                {
                    position.Id = Guid.NewGuid();
                    position.Name = name;
                    position.IsActive = true;
                    ctx.StaffPositions.Add(position);
                    saved = position;
                }
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
                saved = existing;
            }

            await ctx.SaveChangesAsync();

            return Result<StaffPosition>.Ok(saved);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
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

            if (!SchedulingAccess.CanManageCompany(scope, position.CompanyId))
            {
                return Result<bool>.Fail("You can only manage positions for your own company.");
            }

            position.IsActive = false;

            // Anyone whose primary this was would otherwise be left with no active primary, and
            // their contract/allowance tier would silently fall back to the company default.
            // Hand primary to another active position they hold (if any) so the change is a
            // real, visible one rather than an accidental downgrade.
            List<UserPosition> primariesHere = await ctx.UserPositions
                .Where(x => x.StaffPositionId == positionId && x.IsPrimary)
                .ToListAsync();

            if (primariesHere.Count > 0)
            {
                List<string> affectedUserIds = primariesHere.Select(x => x.UserId).ToList();

                List<UserPosition> alternatives = await ctx.UserPositions
                    .Include(x => x.StaffPosition)
                    .Where(x => affectedUserIds.Contains(x.UserId) && x.StaffPositionId != positionId && x.StaffPosition!.IsActive)
                    .OrderBy(x => x.StaffPosition!.SortOrder)
                    .ToListAsync();

                foreach (UserPosition primary in primariesHere)
                {
                    primary.IsPrimary = false;

                    UserPosition? replacement = alternatives.FirstOrDefault(x => x.UserId == primary.UserId);

                    if (replacement is not null)
                    {
                        replacement.IsPrimary = true;
                    }
                }
            }

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

            if (primaryPositionId is Guid primary && !distinctIds.Contains(primary))
            {
                return Result<List<UserPosition>>.Fail("The primary position must be one of the selected positions.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Checked against the *user*, not the selection - an empty selection ("remove all
            // positions") must be authorised just as strictly as adding one.
            if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<List<UserPosition>>.Fail("You can only manage positions for staff in your own company.");
            }

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .Where(x => distinctIds.Contains(x.Id) && x.IsActive)
                .ToListAsync();

            if (positions.Count != distinctIds.Count)
            {
                return Result<List<UserPosition>>.Fail("One or more of the selected positions no longer exists.");
            }

            List<int> companyIds = positions.Select(x => x.CompanyId).Distinct().ToList();

            if (companyIds.Count > 1)
            {
                return Result<List<UserPosition>>.Fail("Positions from different companies cannot be combined.");
            }

            if (companyIds.Count == 1 && !SchedulingAccess.CanManageCompany(scope, companyIds[0]))
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
