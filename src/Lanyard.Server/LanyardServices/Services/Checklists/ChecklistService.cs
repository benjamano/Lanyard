using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Checklists;

public class ChecklistService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<ChecklistService> logger) : IChecklistService
{
    private const string LocationOnly = "You can only change checklists for the location you manage.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<ChecklistService> _logger = logger;

    public async Task<Result<List<Checklist>>> GetChecklistsAsync(LocationScope scope, int locationId, bool includeArchived = false)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<Checklist>>.Fail("You can only see checklists for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Checklist> checklists = await ctx.Checklists
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Tasks.Where(t => t.IsActive))
                .Include(x => x.StaffPosition)
                .Where(x => x.LocationId == locationId && (includeArchived || x.IsActive))
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            foreach (Checklist checklist in checklists)
            {
                checklist.Tasks = [.. checklist.Tasks.OrderBy(x => x.SortOrder)];
            }

            return Result<List<Checklist>>.Ok(checklists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checklists for {LocationId}", locationId);
            return Result<List<Checklist>>.Fail($"Failed to load checklists: {ex.Message}");
        }
    }

    public async Task<Result<Checklist>> GetChecklistAsync(LocationScope scope, Guid checklistId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Checklist? checklist = await ctx.Checklists
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Tasks.Where(t => t.IsActive))
                .Include(x => x.StaffPosition)
                .FirstOrDefaultAsync(x => x.Id == checklistId);

            if (checklist is null || !SchedulingAccess.CanManageLocation(scope, checklist.LocationId))
            {
                return Result<Checklist>.Fail("That checklist wasn't found.");
            }

            checklist.Tasks = [.. checklist.Tasks.OrderBy(x => x.SortOrder)];

            return Result<Checklist>.Ok(checklist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checklist {ChecklistId}", checklistId);
            return Result<Checklist>.Fail($"Failed to load the checklist: {ex.Message}");
        }
    }

    public async Task<Result<Checklist>> SaveChecklistAsync(LocationScope scope, Checklist checklist, string actingUserId)
    {
        try
        {
            if (ChecklistRules.Validate(checklist) is string invalid)
            {
                return Result<Checklist>.Fail(invalid);
            }

            if (!SchedulingAccess.CanManageLocation(scope, checklist.LocationId))
            {
                return Result<Checklist>.Fail(LocationOnly);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == checklist.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<Checklist>.Fail("Location not found.");
            }

            Checklist? existing = null;

            if (checklist.Id != Guid.Empty)
            {
                existing = await ctx.Checklists.FirstOrDefaultAsync(x => x.Id == checklist.Id && x.LocationId == checklist.LocationId);

                if (existing is null)
                {
                    return Result<Checklist>.Fail("That checklist wasn't found.");
                }
            }

            // A position must be the location's company's and active - unless the checklist already
            // used it, so archiving a position doesn't stop the rest of the checklist being edited.
            if (checklist.StaffPositionId is Guid positionId && positionId != existing?.StaffPositionId)
            {
                bool positionOk = await ctx.StaffPositions
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(x => x.Id == positionId && x.CompanyId == location.CompanyId && x.IsActive);

                if (!positionOk)
                {
                    return Result<Checklist>.Fail("That position isn't available any more. Choose another.");
                }
            }

            DateTime now = DateTime.UtcNow;
            Checklist target;

            if (existing is null)
            {
                int nextSort = await ctx.Checklists
                    .Where(x => x.LocationId == checklist.LocationId)
                    .Select(x => (int?)x.SortOrder)
                    .MaxAsync() ?? -1;

                target = new Checklist
                {
                    CompanyId = location.CompanyId,
                    LocationId = location.Id,
                    Name = string.Empty,
                    SortOrder = nextSort + 1,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                ctx.Checklists.Add(target);
            }
            else
            {
                target = existing;
                target.UpdateDate = now;
                target.UpdateByUserId = actingUserId;
            }

            target.Name = checklist.Name.Trim();
            target.Description = string.IsNullOrWhiteSpace(checklist.Description) ? null : checklist.Description.Trim();
            target.Days = checklist.Days & ChecklistDays.EveryDay;
            target.OnlyWhenOpen = checklist.OnlyWhenOpen;
            target.DueRule = checklist.DueRule;
            target.DueOffsetMinutes = checklist.DueRule is ChecklistDueRule.AfterOpening or ChecklistDueRule.BeforeClosing ? checklist.DueOffsetMinutes : 0;
            target.DueTime = checklist.DueRule == ChecklistDueRule.FixedTime ? checklist.DueTime : null;
            target.StaffPositionId = checklist.StaffPositionId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Checklist {ChecklistId} saved for location {LocationId} by {UserId}", target.Id, target.LocationId, actingUserId);

            return Result<Checklist>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save checklist {ChecklistId}", checklist.Id);
            return Result<Checklist>.Fail($"Failed to save the checklist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetArchivedAsync(LocationScope scope, Guid checklistId, bool archived, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Checklist? checklist = await ctx.Checklists.FirstOrDefaultAsync(x => x.Id == checklistId);

            if (checklist is null || !SchedulingAccess.CanManageLocation(scope, checklist.LocationId))
            {
                return Result<bool>.Fail("That checklist wasn't found.");
            }

            checklist.IsActive = !archived;
            checklist.UpdateDate = DateTime.UtcNow;
            checklist.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Checklist {ChecklistId} {Change} by {UserId}", checklistId, archived ? "archived" : "restored", actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive checklist {ChecklistId}", checklistId);
            return Result<bool>.Fail($"Failed to change the checklist: {ex.Message}");
        }
    }

    public async Task<Result<ChecklistTask>> SaveTaskAsync(LocationScope scope, ChecklistTask task, string actingUserId)
    {
        try
        {
            if (ChecklistRules.Validate(task) is string invalid)
            {
                return Result<ChecklistTask>.Fail(invalid);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Checklist? checklist = await ctx.Checklists
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == task.ChecklistId);

            if (checklist is null || !SchedulingAccess.CanManageLocation(scope, checklist.LocationId))
            {
                return Result<ChecklistTask>.Fail("That checklist wasn't found.");
            }

            DateTime now = DateTime.UtcNow;
            ChecklistTask target;

            if (task.Id == Guid.Empty)
            {
                List<int> sorts = await ctx.ChecklistTasks
                    .Where(x => x.ChecklistId == checklist.Id && x.IsActive)
                    .Select(x => x.SortOrder)
                    .ToListAsync();

                if (sorts.Count >= ChecklistRules.MaxTasks)
                {
                    return Result<ChecklistTask>.Fail($"A checklist can have at most {ChecklistRules.MaxTasks} tasks.");
                }

                target = new ChecklistTask
                {
                    ChecklistId = checklist.Id,
                    Title = string.Empty,
                    SortOrder = sorts.Count == 0 ? 0 : sorts.Max() + 1,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                ctx.ChecklistTasks.Add(target);
            }
            else
            {
                ChecklistTask? existing = await ctx.ChecklistTasks.FirstOrDefaultAsync(x => x.Id == task.Id && x.ChecklistId == checklist.Id && x.IsActive);

                if (existing is null)
                {
                    return Result<ChecklistTask>.Fail("That task wasn't found.");
                }

                target = existing;
                target.UpdateDate = now;
                target.UpdateByUserId = actingUserId;
            }

            target.Title = task.Title.Trim();
            target.Detail = string.IsNullOrWhiteSpace(task.Detail) ? null : task.Detail.Trim();
            target.Response = task.Response;
            target.Unit = task.Response == ChecklistResponse.Number && !string.IsNullOrWhiteSpace(task.Unit) ? task.Unit.Trim() : null;

            await ctx.SaveChangesAsync();

            return Result<ChecklistTask>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save task {TaskId} on checklist {ChecklistId}", task.Id, task.ChecklistId);
            return Result<ChecklistTask>.Fail($"Failed to save the task: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveTaskAsync(LocationScope scope, Guid taskId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChecklistTask? task = await ctx.ChecklistTasks
                .Include(x => x.Checklist)
                .FirstOrDefaultAsync(x => x.Id == taskId && x.IsActive);

            if (task?.Checklist is null || !SchedulingAccess.CanManageLocation(scope, task.Checklist.LocationId))
            {
                return Result<bool>.Fail("That task wasn't found.");
            }

            task.IsActive = false;
            task.UpdateDate = DateTime.UtcNow;
            task.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove checklist task {TaskId}", taskId);
            return Result<bool>.Fail($"Failed to remove the task: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MoveTaskAsync(LocationScope scope, Guid taskId, int direction, string actingUserId)
    {
        try
        {
            if (direction is not (-1 or 1))
            {
                return Result<bool>.Fail("A task can only move one place at a time.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChecklistTask? task = await ctx.ChecklistTasks
                .Include(x => x.Checklist)
                .FirstOrDefaultAsync(x => x.Id == taskId && x.IsActive);

            if (task?.Checklist is null || !SchedulingAccess.CanManageLocation(scope, task.Checklist.LocationId))
            {
                return Result<bool>.Fail("That task wasn't found.");
            }

            List<ChecklistTask> siblings = await ctx.ChecklistTasks
                .Where(x => x.ChecklistId == task.ChecklistId && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.CreateDate)
                .ToListAsync();

            int index = siblings.FindIndex(x => x.Id == taskId);
            int target = index + direction;

            if (target < 0 || target >= siblings.Count)
            {
                return Result<bool>.Ok(false);
            }

            (siblings[index], siblings[target]) = (siblings[target], siblings[index]);

            // Renumber the lot, so tasks that once shared a SortOrder end up strictly ordered.
            DateTime now = DateTime.UtcNow;

            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].SortOrder != i)
                {
                    siblings[i].SortOrder = i;
                    siblings[i].UpdateDate = now;
                    siblings[i].UpdateByUserId = actingUserId;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move checklist task {TaskId}", taskId);
            return Result<bool>.Fail($"Failed to move the task: {ex.Message}");
        }
    }
}
