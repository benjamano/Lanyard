using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lanyard.Application.Services.Scheduling;

public class StaffPositionService(
    IDbContextFactory<ApplicationDbContext> factory,
    IRequiredTrainingService requiredTrainingService,
    ILogger<StaffPositionService> logger) : IStaffPositionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IRequiredTrainingService _requiredTrainingService = requiredTrainingService;
    private readonly ILogger<StaffPositionService> _logger = logger;

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

    public async Task<Result<List<StaffPositionRequiredCourse>>> GetRequiredCoursesAsync(LocationScope scope, Guid positionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffPosition? position = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == positionId);

            if (position is null)
            {
                return Result<List<StaffPositionRequiredCourse>>.Fail("Position not found.");
            }

            if (!SchedulingAccess.CanManageCompany(scope, position.CompanyId))
            {
                return Result<List<StaffPositionRequiredCourse>>.Fail("You can only manage positions for your own company.");
            }

            List<StaffPositionRequiredCourse> required = await ctx.StaffPositionRequiredCourses
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Course).ThenInclude(x => x!.Location)
                .Where(x => x.StaffPositionId == positionId)
                .OrderBy(x => x.Course!.Name)
                .ToListAsync();

            return Result<List<StaffPositionRequiredCourse>>.Ok(required);
        }
        catch (Exception ex)
        {
            return Result<List<StaffPositionRequiredCourse>>.Fail($"Failed to load the position's required training: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, List<string>>>> GetRequiredCourseNamesAsync(LocationScope scope, int companyId)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, companyId))
            {
                return Result<Dictionary<Guid, List<string>>>.Fail("You can only manage positions for your own company.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var rows = await ctx.StaffPositionRequiredCourses
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.StaffPosition!.CompanyId == companyId && x.Course!.IsActive)
                .OrderBy(x => x.Course!.Name)
                .Select(x => new { x.StaffPositionId, CourseName = x.Course!.Name })
                .ToListAsync();

            Dictionary<Guid, List<string>> names = rows
                .GroupBy(x => x.StaffPositionId)
                .ToDictionary(x => x.Key, x => x.Select(r => r.CourseName).ToList());

            return Result<Dictionary<Guid, List<string>>>.Ok(names);
        }
        catch (Exception ex)
        {
            return Result<Dictionary<Guid, List<string>>>.Fail($"Failed to load the positions' required training: {ex.Message}");
        }
    }

    public async Task<Result<RequiredTrainingAssignResult>> SetRequiredCoursesAsync(LocationScope scope, Guid positionId, List<Guid> courseIds, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffPosition? position = await ctx.StaffPositions.FirstOrDefaultAsync(x => x.Id == positionId);

            if (position is null)
            {
                return Result<RequiredTrainingAssignResult>.Fail("Position not found.");
            }

            if (!SchedulingAccess.CanManageCompany(scope, position.CompanyId))
            {
                return Result<RequiredTrainingAssignResult>.Fail("You can only manage positions for your own company.");
            }

            if (!position.IsActive)
            {
                return Result<RequiredTrainingAssignResult>.Fail("That position has been archived.");
            }

            List<Guid> wanted = courseIds.Distinct().ToList();

            List<Course> chosen = await ctx.Courses
                .Include(x => x.Location)
                .Where(x => wanted.Contains(x.Id))
                .ToListAsync();

            // The link rows aren't ICompanyOwned, so the save-time tenant check doesn't cover
            // them: without this a position could be tied to another company's course, and its
            // name would then show up in this company's claim reasons.
            if (chosen.Count != wanted.Count || chosen.Any(x => x.CompanyId != position.CompanyId))
            {
                return Result<RequiredTrainingAssignResult>.Fail("One of those courses doesn't exist for this company.");
            }

            List<StaffPositionRequiredCourse> existing = await ctx.StaffPositionRequiredCourses
                .Include(x => x.Course).ThenInclude(x => x!.Location)
                .Where(x => x.StaffPositionId == positionId)
                .ToListAsync();

            HashSet<Guid> existingIds = existing.Select(x => x.CourseId).ToHashSet();

            foreach (Course course in chosen.Where(x => !existingIds.Contains(x.Id)))
            {
                if (!CanSeeCourse(scope, course))
                {
                    return Result<RequiredTrainingAssignResult>.Fail($"{course.Name} belongs to another location, so you can't require it.");
                }

                if (!course.IsActive)
                {
                    return Result<RequiredTrainingAssignResult>.Fail($"{course.Name} has been archived, so it can't be required.");
                }
            }

            // Unticked means removed, but only for courses this person can see - a Manager's list
            // doesn't show another location's own courses, so not having ticked them says nothing.
            List<StaffPositionRequiredCourse> removed = existing
                .Where(x => !wanted.Contains(x.CourseId) && CanSeeCourse(scope, x.Course!))
                .ToList();

            List<StaffPositionRequiredCourse> added = chosen
                .Where(x => !existingIds.Contains(x.Id))
                .Select(x => new StaffPositionRequiredCourse
                {
                    Id = Guid.NewGuid(),
                    StaffPositionId = positionId,
                    CourseId = x.Id,
                    CreatedByUserId = actingUserId,
                    CreateDate = DateTime.UtcNow
                })
                .ToList();

            if (removed.Count == 0 && added.Count == 0)
            {
                return Result<RequiredTrainingAssignResult>.Ok(RequiredTrainingAssignResult.Empty);
            }

            ctx.StaffPositionRequiredCourses.RemoveRange(removed);
            ctx.StaffPositionRequiredCourses.AddRange(added);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} changed the training required for position {PositionId}: {Added} added, {Removed} removed",
                actingUserId, positionId, added.Count, removed.Count);

            if (added.Count == 0)
            {
                return Result<RequiredTrainingAssignResult>.Ok(RequiredTrainingAssignResult.Empty);
            }

            // Everyone already in the position gets the new course, so they can do it before it
            // stops them picking up shifts.
            List<string> holders = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.StaffPositionId == positionId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync();

            RequiredTrainingAssignResult training = await AssignRequiredTrainingAsync(scope, actingUserId,
                holders.Select(x => (x, positionId)).ToList(), added.Select(x => x.CourseId).ToList());

            return Result<RequiredTrainingAssignResult>.Ok(training);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<RequiredTrainingAssignResult>.Fail("Someone else has just changed this position's training. Reload and try again.");
        }
        catch (Exception ex)
        {
            return Result<RequiredTrainingAssignResult>.Fail($"Failed to save the position's required training: {ex.Message}");
        }
    }

    public async Task<Result<TrainingCoverage>> GetTrainingCoverageAsync(LocationScope scope, Guid positionId, List<Guid> courseIds)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffPosition? position = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == positionId);

            if (position is null)
            {
                return Result<TrainingCoverage>.Fail("Position not found.");
            }

            if (!SchedulingAccess.CanManageCompany(scope, position.CompanyId))
            {
                return Result<TrainingCoverage>.Fail("You can only manage positions for your own company.");
            }

            // A Manager sees their own location's staff; an Admin sees everyone. Either way each
            // person is judged at the locations they work, since a location's own course only
            // gates shifts there.
            int? locationId = scope.IsAdmin ? null : scope.LocationId;

            IQueryable<string> holderQuery = ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.StaffPositionId == positionId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => x.UserId);

            if (locationId is int only)
            {
                holderQuery = holderQuery.Where(userId => ctx.UserLocationMemberships.Any(m => m.UserId == userId && m.LocationId == only));
            }

            List<string> holders = await holderQuery.Distinct().ToListAsync();
            string? locationName = locationId is null ? null : scope.LocationDisplayName;

            List<Guid> wanted = courseIds.Distinct().ToList();

            List<ShiftClaimRules.RequiredCourse> courses = await ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => wanted.Contains(x.Id) && x.IsActive && x.CompanyId == position.CompanyId)
                .Select(x => new ShiftClaimRules.RequiredCourse(positionId, x.Id, x.Name, x.RecurrenceMonths, x.LocationId, x.IsShared))
                .ToListAsync();

            if (holders.Count == 0 || courses.Count == 0)
            {
                return Result<TrainingCoverage>.Ok(new TrainingCoverage(holders.Count, holders.Count, locationName));
            }

            Dictionary<string, Dictionary<Guid, ShiftClaimRules.TrainingRecord>> records =
                await ShiftClaimRules.LoadTrainingRecordsAsync(ctx, holders, courses.Select(x => x.CourseId).ToList());

            var memberships = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => holders.Contains(x.UserId))
                .Select(x => new { x.UserId, x.LocationId })
                .ToListAsync();

            DateTime now = DateTime.UtcNow;

            int upToDate = holders.Count(holder =>
            {
                Dictionary<Guid, ShiftClaimRules.TrainingRecord> record = records.GetValueOrDefault(holder) ?? [];
                List<int?> workAt = locationId is int only
                    ? [only]
                    : memberships.Where(x => x.UserId == holder).Select(x => (int?)x.LocationId).DefaultIfEmpty(null).ToList();

                return workAt.All(at => ShiftClaimRules.CourseGaps(courses, record, at, now).Count == 0);
            });

            return Result<TrainingCoverage>.Ok(new TrainingCoverage(holders.Count, upToDate, locationName));
        }
        catch (Exception ex)
        {
            return Result<TrainingCoverage>.Fail($"Failed to check who is up to date: {ex.Message}");
        }
    }

    // The position change has already been saved, so a failure here is logged rather than
    // returned - it mustn't look as though the positions didn't save.
    private async Task<RequiredTrainingAssignResult> AssignRequiredTrainingAsync(LocationScope scope, string actingUserId,
        IReadOnlyCollection<(string UserId, Guid PositionId)> holdings, IReadOnlyCollection<Guid>? onlyCourseIds)
    {
        if (holdings.Count == 0)
        {
            return RequiredTrainingAssignResult.Empty;
        }

        Result<RequiredTrainingAssignResult> result = await _requiredTrainingService.AssignMissingAsync(scope, actingUserId, holdings, onlyCourseIds);

        if (!result.IsSuccess || result.Data is null)
        {
            _logger.LogWarning("Positions saved, but assigning their required training failed: {Error}", result.Error);
            return RequiredTrainingAssignResult.Empty;
        }

        return result.Data;
    }

    // The same visibility rule as CourseService.GetCoursesAsync: an Admin sees every course, a
    // Manager their own location's and any shared one in their company.
    private static bool CanSeeCourse(LocationScope scope, Course course) =>
        scope.IsAdmin
        || course.LocationId == scope.LocationId
        || (course.IsShared && course.Location is not null && course.Location.CompanyId == scope.CompanyId);

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

    public async Task<Result<UserPositionsSaved>> SetUserPositionsAsync(LocationScope scope, string userId, List<Guid> positionIds, Guid? primaryPositionId, string actingUserId)
    {
        try
        {
            List<Guid> distinctIds = positionIds.Distinct().ToList();

            if (primaryPositionId is Guid primary && !distinctIds.Contains(primary))
            {
                return Result<UserPositionsSaved>.Fail("The primary position must be one of the selected positions.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Checked against the *user*, not the selection - an empty selection ("remove all
            // positions") must be authorised just as strictly as adding one.
            if (!await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<UserPositionsSaved>.Fail("You can only manage positions for staff in your own company.");
            }

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .Where(x => distinctIds.Contains(x.Id) && x.IsActive)
                .ToListAsync();

            if (positions.Count != distinctIds.Count)
            {
                return Result<UserPositionsSaved>.Fail("One or more of the selected positions no longer exists.");
            }

            List<int> companyIds = positions.Select(x => x.CompanyId).Distinct().ToList();

            if (companyIds.Count > 1)
            {
                return Result<UserPositionsSaved>.Fail("Positions from different companies cannot be combined.");
            }

            if (companyIds.Count == 1 && !SchedulingAccess.CanManageCompany(scope, companyIds[0]))
            {
                return Result<UserPositionsSaved>.Fail("You can only assign positions from your own company.");
            }

            List<UserPosition> existing = await ctx.UserPositions
                .Where(x => x.UserId == userId)
                .ToListAsync();

            Guid? effectivePrimary = primaryPositionId ?? distinctIds.FirstOrDefault();
            List<Guid> newPositionIds = [];

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
                    newPositionIds.Add(positionId);
                }

                row.IsPrimary = positionId == effectivePrimary;
            }

            await ctx.SaveChangesAsync();

            RequiredTrainingAssignResult training = await AssignRequiredTrainingAsync(scope, actingUserId,
                newPositionIds.Select(x => (userId, x)).ToList(), null);

            Result<List<UserPosition>> saved = await GetUserPositionsAsync(userId);

            return saved.IsSuccess && saved.Data is not null
                ? Result<UserPositionsSaved>.Ok(new UserPositionsSaved(saved.Data, training))
                : Result<UserPositionsSaved>.Fail(saved.Error ?? "Failed to reload the user's positions.");
        }
        catch (Exception ex)
        {
            return Result<UserPositionsSaved>.Fail($"Failed to save the user's positions: {ex.Message}");
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
