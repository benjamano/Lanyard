using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class ShiftTemplateService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<ShiftTemplateService> logger) : IShiftTemplateService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<ShiftTemplateService> _logger = logger;

    // Active templates for one location with slots and positions - what Auto-build reads.
    internal static IQueryable<ShiftTemplate> ForLocation(ApplicationDbContext ctx, int locationId, bool includeArchived = false) =>
        ctx.ShiftTemplates
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Slots)
            .ThenInclude(x => x.StaffPosition)
            .Where(x => x.LocationId == locationId && (includeArchived || x.IsActive));

    public async Task<Result<List<ShiftTemplate>>> GetTemplatesAsync(LocationScope scope, int locationId, bool includeArchived = false)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<ShiftTemplate>>.Fail("You can only see shift templates for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ShiftTemplate> templates = await ForLocation(ctx, locationId, includeArchived).ToListAsync();

            return Result<List<ShiftTemplate>>.Ok(Ordered(templates));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load shift templates for {LocationId}", locationId);
            return Result<List<ShiftTemplate>>.Fail($"Failed to load shift templates: {ex.Message}");
        }
    }

    public async Task<Result<ShiftTemplate>> SaveTemplateAsync(LocationScope scope, ShiftTemplate template, string actingUserId)
    {
        try
        {
            if (ShiftTemplateRules.Validate(template) is string invalid)
            {
                return Result<ShiftTemplate>.Fail(invalid);
            }

            if (!SchedulingAccess.CanManageLocation(scope, template.LocationId))
            {
                return Result<ShiftTemplate>.Fail("You can only change shift templates for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == template.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<ShiftTemplate>.Fail("Location not found.");
            }

            List<Guid> positionIds = template.Slots.Select(x => x.StaffPositionId).Distinct().ToList();

            ShiftTemplate? existing = null;

            if (template.Id != Guid.Empty)
            {
                existing = await ctx.ShiftTemplates
                    .Include(x => x.Slots)
                    .FirstOrDefaultAsync(x => x.Id == template.Id && x.LocationId == template.LocationId);

                if (existing is null)
                {
                    return Result<ShiftTemplate>.Fail("That shift template wasn't found.");
                }
            }

            // A position must be the location's company's and active - unless the template already
            // used it, so archiving a position doesn't stop the rest of the template being edited.
            HashSet<Guid> alreadyUsed = existing?.Slots.Select(x => x.StaffPositionId).ToHashSet() ?? [];

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => positionIds.Contains(x.Id) && x.CompanyId == location.CompanyId)
                .ToListAsync();

            foreach (Guid positionId in positionIds)
            {
                StaffPosition? position = positions.FirstOrDefault(x => x.Id == positionId);

                if (position is null || (!position.IsActive && !alreadyUsed.Contains(positionId)))
                {
                    return Result<ShiftTemplate>.Fail("One of the positions isn't available any more. Choose another.");
                }
            }

            DateTime now = DateTime.UtcNow;
            ShiftTemplate target;

            if (existing is null)
            {
                target = new ShiftTemplate
                {
                    Id = Guid.NewGuid(),
                    CompanyId = location.CompanyId,
                    LocationId = location.Id,
                    Name = string.Empty,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                ctx.ShiftTemplates.Add(target);
            }
            else
            {
                target = existing;
                target.UpdateDate = now;
                target.UpdateByUserId = actingUserId;
                ctx.ShiftTemplateSlots.RemoveRange(target.Slots);
                target.Slots = [];
            }

            target.Name = template.Name.Trim();
            target.DayOfWeek = template.DayOfWeek;
            target.StartTime = template.StartTime;
            target.EndTime = template.EndTime;
            target.BreakMinutes = template.BreakMinutes;
            target.EffectiveFrom = template.EffectiveFrom;
            target.EffectiveTo = template.EffectiveTo;

            foreach (ShiftTemplateSlot slot in template.Slots)
            {
                ShiftTemplateSlot row = new()
                {
                    Id = Guid.NewGuid(),
                    ShiftTemplateId = target.Id,
                    StaffPositionId = slot.StaffPositionId,
                    Headcount = slot.Headcount
                };

                target.Slots.Add(row);
                ctx.ShiftTemplateSlots.Add(row);
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Shift template {TemplateId} saved for {LocationId} by {ActingUserId}", target.Id, target.LocationId, actingUserId);

            return Result<ShiftTemplate>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save shift template for {LocationId}", template.LocationId);
            return Result<ShiftTemplate>.Fail($"Failed to save shift template: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetArchivedAsync(LocationScope scope, Guid templateId, bool archived, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ShiftTemplate? template = await ctx.ShiftTemplates.FirstOrDefaultAsync(x => x.Id == templateId);

            if (template is null || !SchedulingAccess.CanManageLocation(scope, template.LocationId))
            {
                return Result<bool>.Fail("That shift template wasn't found.");
            }

            template.IsActive = !archived;
            template.UpdateDate = DateTime.UtcNow;
            template.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Shift template {TemplateId} {Action} by {ActingUserId}", templateId, archived ? "archived" : "restored", actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change shift template {TemplateId}", templateId);
            return Result<bool>.Fail($"Failed to change shift template: {ex.Message}");
        }
    }

    // Monday first, then by start time and name - the order the week reads in.
    internal static List<ShiftTemplate> Ordered(IEnumerable<ShiftTemplate> templates) =>
        templates
            .OrderBy(x => ((int)x.DayOfWeek + 6) % 7)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
