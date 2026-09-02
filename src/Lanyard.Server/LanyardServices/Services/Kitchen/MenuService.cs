using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

public class MenuService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<MenuService> logger) : IMenuService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<MenuService> _logger = logger;

    public async Task<Result<MenuDto>> GetPublicMenuAsync(int locationId, int expectedCompanyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(l => l.Id == locationId && l.IsActive);

            // Same "not found" for a missing location and one belonging to another tenant, so
            // the endpoint cannot be walked to discover which location ids exist elsewhere.
            if (location is null || location.CompanyId != expectedCompanyId)
            {
                return Result<MenuDto>.Fail("Location not found.");
            }

            if (!location.OrderingEnabled)
            {
                return Result<MenuDto>.Fail("Ordering is not enabled for this location.");
            }

            List<MenuCategoryDto> categories = await ctx.MenuCategories
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.LocationId == locationId && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .Select(c => new MenuCategoryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    SortOrder = c.SortOrder,
                    // Unavailable items are still returned, with IsAvailable false, so the phone
                    // can show them greyed out. Dropping them would make a sold-out item look
                    // like it never existed, which reads as a broken menu rather than a sold-out
                    // dish - and would silently reshuffle the list under a browsing customer.
                    //
                    // Items whose allergens have not been declared are a different matter and are
                    // withheld entirely: selling food at a distance requires the declaration
                    // before purchase, and showing the dish without it would be offering
                    // something that cannot lawfully be sold.
                    Items = c.Items
                        .Where(i => i.IsActive && i.AllergensConfirmed)
                        .OrderBy(i => i.SortOrder)
                        .ThenBy(i => i.Name)
                        .Select(i => new MenuItemDto
                        {
                            Id = i.Id,
                            Name = i.Name,
                            Description = i.Description,
                            PriceCents = i.PriceCents,
                            IsAvailable = i.IsAvailable,
                            HasImage = i.ImageFileId != null,
                            SortOrder = i.SortOrder,
                            ContainsAllergens = i.ContainsAllergens,
                            MayContainAllergens = i.MayContainAllergens,

                            // An option whose allergens nobody has confirmed is withheld rather
                            // than offered with a blank declaration, exactly as an unconfirmed
                            // dish is - blank must never read as "contains nothing". A group left
                            // with no confirmed choices is dropped, which the order validator
                            // then treats as "this dish cannot currently be ordered".
                            OptionGroups = i.OptionGroups
                                .Where(g => g.IsActive && g.Options.Any(o => o.IsActive && o.AllergensConfirmed))
                                .OrderBy(g => g.SortOrder)
                                .ThenBy(g => g.Name)
                                .Select(g => new MenuItemOptionGroupDto
                                {
                                    Id = g.Id,
                                    Name = g.Name,
                                    MinSelections = g.MinSelections,
                                    MaxSelections = g.MaxSelections,
                                    SortOrder = g.SortOrder,
                                    Options = g.Options
                                        .Where(o => o.IsActive && o.AllergensConfirmed)
                                        .OrderBy(o => o.SortOrder)
                                        .ThenBy(o => o.Name)
                                        .Select(o => new MenuItemOptionDto
                                        {
                                            Id = o.Id,
                                            Name = o.Name,
                                            PriceDeltaCents = o.PriceDeltaCents,
                                            IsAvailable = o.IsAvailable,
                                            SortOrder = o.SortOrder,
                                            ContainsAllergens = o.ContainsAllergens,
                                            MayContainAllergens = o.MayContainAllergens
                                        })
                                        .ToList()
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToListAsync();

            return Result<MenuDto>.Ok(new MenuDto
            {
                LocationId = locationId,
                MenuVersion = location.MenuVersion,
                Categories = categories
            });
        }
        catch (Exception ex)
        {
            return Result<MenuDto>.Fail($"Failed to retrieve menu: {ex.Message}");
        }
    }

    public async Task<Result<List<MenuCategory>>> GetCategoriesForLocationAsync(int locationId, bool includeInactive)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<MenuCategory> categories = await ctx.MenuCategories
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.LocationId == locationId && (includeInactive || c.IsActive))
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .ToListAsync();

            return Result<List<MenuCategory>>.Ok(categories);
        }
        catch (Exception ex)
        {
            return Result<List<MenuCategory>>.Fail($"Failed to retrieve menu categories: {ex.Message}");
        }
    }

    public async Task<Result<List<MenuItem>>> GetItemsForLocationAsync(int locationId, bool includeInactive)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<MenuItem> items = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(i => i.Category != null
                    && i.Category.LocationId == locationId
                    && (includeInactive || i.IsActive))
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Name)
                .ToListAsync();

            return Result<List<MenuItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            return Result<List<MenuItem>>.Fail($"Failed to retrieve menu items: {ex.Message}");
        }
    }

    public async Task<Result<MenuCategory>> SaveCategoryAsync(MenuCategory category)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(category.Name))
            {
                return Result<MenuCategory>.Fail("Category name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ctx.Locations.AnyAsync(l => l.Id == category.LocationId && l.IsActive))
            {
                return Result<MenuCategory>.Fail("Location not found.");
            }

            DateTime now = DateTime.UtcNow;
            MenuCategory entity;

            if (category.Id == 0)
            {
                entity = new MenuCategory
                {
                    LocationId = category.LocationId,
                    Name = category.Name.Trim(),
                    SortOrder = category.SortOrder,
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.MenuCategories.AddAsync(entity);
            }
            else
            {
                MenuCategory? existing = await ctx.MenuCategories.FirstOrDefaultAsync(c => c.Id == category.Id);

                if (existing is null)
                {
                    return Result<MenuCategory>.Fail("Category not found.");
                }

                existing.Name = category.Name.Trim();
                existing.SortOrder = category.SortOrder;
                existing.IsActive = category.IsActive;
                existing.UpdateDate = now;
                entity = existing;
            }

            await BumpMenuVersionAsync(ctx, entity.LocationId, now);
            await ctx.SaveChangesAsync();

            return Result<MenuCategory>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<MenuCategory>.Fail($"Failed to save menu category: {ex.Message}");
        }
    }

    public async Task<Result<MenuItem>> SaveItemAsync(MenuItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return Result<MenuItem>.Fail("Item name is required.");
            }

            if (item.PriceCents < 0)
            {
                return Result<MenuItem>.Fail("Price cannot be negative.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuCategory? category = await ctx.MenuCategories
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(c => c.Id == item.CategoryId);

            if (category is null)
            {
                return Result<MenuItem>.Fail("Category not found.");
            }

            DateTime now = DateTime.UtcNow;
            MenuItem entity;

            if (item.Id == 0)
            {
                entity = new MenuItem
                {
                    CategoryId = item.CategoryId,
                    Name = item.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                    PriceCents = item.PriceCents,
                    ImageFileId = item.ImageFileId,
                    IsAvailable = item.IsAvailable,
                    SortOrder = item.SortOrder,
                    ContainsAllergens = item.ContainsAllergens,
                    MayContainAllergens = item.MayContainAllergens,
                    AllergensConfirmed = item.AllergensConfirmed,
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.MenuItems.AddAsync(entity);
            }
            else
            {
                MenuItem? existing = await ctx.MenuItems.FirstOrDefaultAsync(i => i.Id == item.Id);

                if (existing is null)
                {
                    return Result<MenuItem>.Fail("Item not found.");
                }

                existing.CategoryId = item.CategoryId;
                existing.Name = item.Name.Trim();
                existing.Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim();
                existing.PriceCents = item.PriceCents;
                existing.ImageFileId = item.ImageFileId;
                existing.IsAvailable = item.IsAvailable;
                existing.SortOrder = item.SortOrder;
                existing.ContainsAllergens = item.ContainsAllergens;
                existing.MayContainAllergens = item.MayContainAllergens;
                existing.AllergensConfirmed = item.AllergensConfirmed;
                existing.IsActive = item.IsActive;
                existing.UpdateDate = now;
                entity = existing;
            }

            await BumpMenuVersionAsync(ctx, category.LocationId, now);
            await ctx.SaveChangesAsync();

            return Result<MenuItem>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<MenuItem>.Fail($"Failed to save menu item: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetItemAvailabilityAsync(int itemId, bool isAvailable)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuItem? item = await ctx.MenuItems.FirstOrDefaultAsync(i => i.Id == itemId);

            if (item is null)
            {
                return Result<bool>.Fail("Item not found.");
            }

            MenuCategory? category = await ctx.MenuCategories
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(c => c.Id == item.CategoryId);

            if (category is null)
            {
                return Result<bool>.Fail("Category not found.");
            }

            DateTime now = DateTime.UtcNow;

            item.IsAvailable = isAvailable;
            item.UpdateDate = now;

            // The version bump is what makes this visible to a phone already browsing the menu.
            await BumpMenuVersionAsync(ctx, category.LocationId, now);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Menu item {ItemId} at location {LocationId} marked {Availability}",
                itemId, category.LocationId, isAvailable ? "available" : "unavailable");

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to update item availability: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateCategoryAsync(int categoryId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuCategory? category = await ctx.MenuCategories.FirstOrDefaultAsync(c => c.Id == categoryId);

            if (category is null)
            {
                return Result<bool>.Fail("Category not found.");
            }

            DateTime now = DateTime.UtcNow;

            category.IsActive = false;
            category.UpdateDate = now;

            await BumpMenuVersionAsync(ctx, category.LocationId, now);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate category: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateItemAsync(int itemId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuItem? item = await ctx.MenuItems.FirstOrDefaultAsync(i => i.Id == itemId);

            if (item is null)
            {
                return Result<bool>.Fail("Item not found.");
            }

            int? locationId = await ctx.MenuCategories
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.Id == item.CategoryId)
                .Select(c => (int?)c.LocationId)
                .FirstOrDefaultAsync();

            DateTime now = DateTime.UtcNow;

            item.IsActive = false;
            item.UpdateDate = now;

            if (locationId is int id)
            {
                await BumpMenuVersionAsync(ctx, id, now);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate item: {ex.Message}");
        }
    }

    public async Task<Result<Guid>> GetItemImageFileIdAsync(int itemId, int expectedCompanyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Guid? fileId = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(i => i.Id == itemId
                    && i.IsActive
                    && i.Category != null
                    && i.Category.Location != null
                    && i.Category.Location.CompanyId == expectedCompanyId)
                .Select(i => i.ImageFileId)
                .FirstOrDefaultAsync();

            return fileId is Guid id
                ? Result<Guid>.Ok(id)
                : Result<Guid>.Fail("Item has no image.");
        }
        catch (Exception ex)
        {
            return Result<Guid>.Fail($"Failed to resolve item image: {ex.Message}");
        }
    }

    public async Task<Result<List<MenuItemOptionGroup>>> GetOptionGroupsForItemAsync(int menuItemId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<MenuItemOptionGroup> groups = await ctx.MenuItemOptionGroups
                .AsNoTracking()
                .TagWithCallSite()
                .Where(g => g.MenuItemId == menuItemId && g.IsActive)
                .OrderBy(g => g.SortOrder)
                .ThenBy(g => g.Name)
                .Include(g => g.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).ThenBy(o => o.Name))
                .ToListAsync();

            return Result<List<MenuItemOptionGroup>>.Ok(groups);
        }
        catch (Exception ex)
        {
            return Result<List<MenuItemOptionGroup>>.Fail($"Failed to load choices: {ex.Message}");
        }
    }

    public async Task<Result<MenuItemOptionGroup>> SaveOptionGroupAsync(MenuItemOptionGroup group)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                return Result<MenuItemOptionGroup>.Fail("Give the choice a name, for example \"Choose your side\".");
            }

            if (group.MinSelections < 0 || group.MaxSelections < 1 || group.MinSelections > group.MaxSelections)
            {
                return Result<MenuItemOptionGroup>.Fail("The number of choices allowed doesn't make sense.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int? locationId = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(i => i.Id == group.MenuItemId)
                .Select(i => (int?)i.Category!.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is null)
            {
                return Result<MenuItemOptionGroup>.Fail("Dish not found.");
            }

            DateTime now = DateTime.UtcNow;
            MenuItemOptionGroup entity;

            if (group.Id == 0)
            {
                entity = new MenuItemOptionGroup
                {
                    MenuItemId = group.MenuItemId,
                    Name = group.Name.Trim(),
                    MinSelections = group.MinSelections,
                    MaxSelections = group.MaxSelections,
                    SortOrder = group.SortOrder,
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.MenuItemOptionGroups.AddAsync(entity);
            }
            else
            {
                MenuItemOptionGroup? existing = await ctx.MenuItemOptionGroups.FirstOrDefaultAsync(g => g.Id == group.Id);

                if (existing is null)
                {
                    return Result<MenuItemOptionGroup>.Fail("Choice not found.");
                }

                entity = existing;
                entity.Name = group.Name.Trim();
                entity.MinSelections = group.MinSelections;
                entity.MaxSelections = group.MaxSelections;
                entity.SortOrder = group.SortOrder;
                entity.UpdateDate = now;
            }

            await BumpMenuVersionAsync(ctx, locationId.Value, now);
            await ctx.SaveChangesAsync();

            return Result<MenuItemOptionGroup>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<MenuItemOptionGroup>.Fail($"Failed to save the choice: {ex.Message}");
        }
    }

    public async Task<Result<MenuItemOption>> SaveOptionAsync(MenuItemOption option)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(option.Name))
            {
                return Result<MenuItemOption>.Fail("Give the choice a name, for example \"Beans\".");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int? locationId = await ctx.MenuItemOptionGroups
                .AsNoTracking()
                .TagWithCallSite()
                .Where(g => g.Id == option.OptionGroupId)
                .Select(g => (int?)g.MenuItem!.Category!.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is null)
            {
                return Result<MenuItemOption>.Fail("Choice group not found.");
            }

            DateTime now = DateTime.UtcNow;
            MenuItemOption entity;

            if (option.Id == 0)
            {
                entity = new MenuItemOption
                {
                    OptionGroupId = option.OptionGroupId,
                    Name = option.Name.Trim(),
                    PriceDeltaCents = option.PriceDeltaCents,
                    IsAvailable = option.IsAvailable,
                    SortOrder = option.SortOrder,
                    ContainsAllergens = option.ContainsAllergens,
                    MayContainAllergens = option.MayContainAllergens,
                    AllergensConfirmed = option.AllergensConfirmed,
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.MenuItemOptions.AddAsync(entity);
            }
            else
            {
                MenuItemOption? existing = await ctx.MenuItemOptions.FirstOrDefaultAsync(o => o.Id == option.Id);

                if (existing is null)
                {
                    return Result<MenuItemOption>.Fail("Choice not found.");
                }

                entity = existing;
                entity.Name = option.Name.Trim();
                entity.PriceDeltaCents = option.PriceDeltaCents;
                entity.IsAvailable = option.IsAvailable;
                entity.SortOrder = option.SortOrder;
                entity.ContainsAllergens = option.ContainsAllergens;
                entity.MayContainAllergens = option.MayContainAllergens;
                entity.AllergensConfirmed = option.AllergensConfirmed;
                entity.UpdateDate = now;
            }

            await BumpMenuVersionAsync(ctx, locationId.Value, now);
            await ctx.SaveChangesAsync();

            return Result<MenuItemOption>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<MenuItemOption>.Fail($"Failed to save the choice: {ex.Message}");
        }
    }

    /// <summary>
    /// 86's a single choice. Separate from deactivating it so running out of beans for one
    /// service does not require rebuilding the choice tomorrow.
    /// </summary>
    public async Task<Result<bool>> SetOptionAvailabilityAsync(int optionId, bool isAvailable)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuItemOption? option = await ctx.MenuItemOptions.FirstOrDefaultAsync(o => o.Id == optionId);

            if (option is null)
            {
                return Result<bool>.Fail("Choice not found.");
            }

            DateTime now = DateTime.UtcNow;
            option.IsAvailable = isAvailable;
            option.UpdateDate = now;

            int? locationId = await ctx.MenuItemOptionGroups
                .AsNoTracking()
                .TagWithCallSite()
                .Where(g => g.Id == option.OptionGroupId)
                .Select(g => (int?)g.MenuItem!.Category!.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is not null)
            {
                // Bumped so a phone already browsing refetches and greys the choice out, the same
                // way 86'ing a whole dish reaches a customer mid-order.
                await BumpMenuVersionAsync(ctx, locationId.Value, now);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to update the choice: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateOptionGroupAsync(int groupId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuItemOptionGroup? group = await ctx.MenuItemOptionGroups.FirstOrDefaultAsync(g => g.Id == groupId);

            if (group is null)
            {
                return Result<bool>.Fail("Choice not found.");
            }

            DateTime now = DateTime.UtcNow;

            // Soft-deleted, like everything else here: past orders reference these rows, and the
            // snapshots on them are what keeps an old ticket readable.
            group.IsActive = false;
            group.UpdateDate = now;

            int? locationId = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(i => i.Id == group.MenuItemId)
                .Select(i => (int?)i.Category!.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is not null)
            {
                await BumpMenuVersionAsync(ctx, locationId.Value, now);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the choice: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateOptionAsync(int optionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            MenuItemOption? option = await ctx.MenuItemOptions.FirstOrDefaultAsync(o => o.Id == optionId);

            if (option is null)
            {
                return Result<bool>.Fail("Choice not found.");
            }

            DateTime now = DateTime.UtcNow;
            option.IsActive = false;
            option.UpdateDate = now;

            int? locationId = await ctx.MenuItemOptionGroups
                .AsNoTracking()
                .TagWithCallSite()
                .Where(g => g.Id == option.OptionGroupId)
                .Select(g => (int?)g.MenuItem!.Category!.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is not null)
            {
                await BumpMenuVersionAsync(ctx, locationId.Value, now);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the choice: {ex.Message}");
        }
    }

    // Tracked (not AsNoTracking) on purpose: the caller saves this alongside its own change so
    // the menu edit and the version bump land in one transaction. A version that could be
    // written without the change it describes would let a phone cache a menu it never saw.
    private static async Task BumpMenuVersionAsync(ApplicationDbContext ctx, int locationId, DateTime now)
    {
        Location? location = await ctx.Locations.FirstOrDefaultAsync(l => l.Id == locationId);

        if (location is not null)
        {
            location.MenuVersion = now;
        }
    }
}
