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
                            MayContainAllergens = i.MayContainAllergens
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
