using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Parties;

public class PartySettingsService(IDbContextFactory<ApplicationDbContext> factory) : IPartySettingsService
{
    private const string NoAccess = "You can only change party settings for your own location.";

    // A day is the longest anything here could sensibly be.
    private const int MaxMinutes = 24 * 60;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<PartyLocationSettings>> GetSettingsAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<PartyLocationSettings>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyLocationSettings? settings = await ctx.PartyLocationSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.LocationId == locationId);

            return Result<PartyLocationSettings>.Ok(settings ?? new PartyLocationSettings { LocationId = locationId });
        }
        catch (Exception ex)
        {
            return Result<PartyLocationSettings>.Fail($"Failed to load party settings: {ex.Message}");
        }
    }

    public async Task<Result<PartyLocationSettings>> SaveSettingsAsync(LocationScope scope, PartyLocationSettings settings, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, settings.LocationId))
            {
                return Result<PartyLocationSettings>.Fail(NoAccess);
            }

            if (settings.DefaultLengthMinutes is < 15 or > MaxMinutes)
            {
                return Result<PartyLocationSettings>.Fail("A party must last between 15 minutes and 24 hours.");
            }

            if (settings.EatTimeOffsetMinutes is int eat && (eat < 0 || eat > settings.DefaultLengthMinutes))
            {
                return Result<PartyLocationSettings>.Fail("The eating time must fall within the party.");
            }

            if (settings.LaserTagOffsetMinutes is int laser && (laser < 0 || laser > settings.DefaultLengthMinutes))
            {
                return Result<PartyLocationSettings>.Fail("The laser tag time must fall within the party.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyLocationSettings? existing = await ctx.PartyLocationSettings.FirstOrDefaultAsync(x => x.LocationId == settings.LocationId);

            if (existing is null)
            {
                existing = new PartyLocationSettings { Id = Guid.NewGuid(), LocationId = settings.LocationId };
                ctx.PartyLocationSettings.Add(existing);
            }

            existing.DefaultLengthMinutes = settings.DefaultLengthMinutes;
            existing.EatTimeOffsetMinutes = settings.EatTimeOffsetMinutes;
            existing.LaserTagOffsetMinutes = settings.LaserTagOffsetMinutes;
            existing.UpdateDate = DateTime.UtcNow;
            existing.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<PartyLocationSettings>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<PartyLocationSettings>.Fail($"Failed to save party settings: {ex.Message}");
        }
    }

    public async Task<Result<List<PartyMenuItem>>> GetMenuAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<PartyMenuItem>>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<PartyMenuItem> items = await ctx.PartyMenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.IsActive)
                .OrderBy(x => x.Kind)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            return Result<List<PartyMenuItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            return Result<List<PartyMenuItem>>.Fail($"Failed to load the party menu: {ex.Message}");
        }
    }

    public async Task<Result<PartyMenuItem>> SaveMenuItemAsync(LocationScope scope, PartyMenuItem item, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, item.LocationId))
            {
                return Result<PartyMenuItem>.Fail(NoAccess);
            }

            string name = item.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                return Result<PartyMenuItem>.Fail("Enter a name for the menu item.");
            }

            string? description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim();
            string? allergens = string.IsNullOrWhiteSpace(item.AllergenText) ? null : item.AllergenText.Trim();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyMenuItem saved;

            if (item.Id == Guid.Empty)
            {
                int nextSort = await ctx.PartyMenuItems
                    .Where(x => x.LocationId == item.LocationId && x.Kind == item.Kind && x.IsActive)
                    .Select(x => (int?)x.SortOrder)
                    .MaxAsync() ?? -1;

                saved = new PartyMenuItem
                {
                    Id = Guid.NewGuid(),
                    LocationId = item.LocationId,
                    Kind = item.Kind,
                    SortOrder = nextSort + 1,
                    CreateDate = DateTime.UtcNow,
                    UpdateByUserId = actingUserId
                };

                ctx.PartyMenuItems.Add(saved);
            }
            else
            {
                PartyMenuItem? existing = await ctx.PartyMenuItems.FirstOrDefaultAsync(x => x.Id == item.Id && x.IsActive);

                if (existing is null || existing.LocationId != item.LocationId)
                {
                    return Result<PartyMenuItem>.Fail("Menu item not found.");
                }

                existing.UpdateDate = DateTime.UtcNow;
                existing.UpdateByUserId = actingUserId;
                saved = existing;
            }

            saved.Name = name;
            saved.Description = description;
            saved.AllergenText = allergens;

            await ctx.SaveChangesAsync();

            return Result<PartyMenuItem>.Ok(saved);
        }
        catch (Exception ex)
        {
            return Result<PartyMenuItem>.Fail($"Failed to save the menu item: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveMenuItemAsync(LocationScope scope, Guid itemId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyMenuItem? item = await ctx.PartyMenuItems.FirstOrDefaultAsync(x => x.Id == itemId && x.IsActive);

            if (item is null)
            {
                return Result<bool>.Fail("Menu item not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, item.LocationId))
            {
                return Result<bool>.Fail(NoAccess);
            }

            item.IsActive = false;
            item.UpdateDate = DateTime.UtcNow;
            item.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the menu item: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MoveMenuItemAsync(LocationScope scope, Guid itemId, int direction)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyMenuItem? item = await ctx.PartyMenuItems.FirstOrDefaultAsync(x => x.Id == itemId && x.IsActive);

            if (item is null)
            {
                return Result<bool>.Fail("Menu item not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, item.LocationId))
            {
                return Result<bool>.Fail(NoAccess);
            }

            List<PartyMenuItem> siblings = await ctx.PartyMenuItems
                .Where(x => x.LocationId == item.LocationId && x.Kind == item.Kind && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            int index = siblings.FindIndex(x => x.Id == item.Id);
            int target = index + Math.Sign(direction);

            if (target < 0 || target >= siblings.Count)
            {
                return Result<bool>.Ok(false);
            }

            (siblings[index], siblings[target]) = (siblings[target], siblings[index]);

            // Renumber the whole kind, so gaps or duplicate sort orders left by removals can't
            // make a move appear to do nothing.
            for (int i = 0; i < siblings.Count; i++)
            {
                siblings[i].SortOrder = i;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to move the menu item: {ex.Message}");
        }
    }
}
