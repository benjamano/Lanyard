using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Reads and maintains a venue's food menu. The customer-facing read returns only what is
/// currently orderable; the staff-facing reads return everything so an item can be brought back.
/// </summary>
public interface IMenuService
{
    /// <summary>
    /// Public menu for one venue: active categories and items only, plus the location's menu
    /// version. <paramref name="expectedCompanyId"/> is the tenant whose site is asking, checked
    /// against the location's owner so one company's site can never render another's menu.
    /// </summary>
    Task<Result<MenuDto>> GetPublicMenuAsync(int locationId, int expectedCompanyId);

    /// <summary>Staff view: includes inactive categories/items, which the public read hides.</summary>
    Task<Result<List<MenuCategory>>> GetCategoriesForLocationAsync(int locationId, bool includeInactive);

    Task<Result<List<MenuItem>>> GetItemsForLocationAsync(int locationId, bool includeInactive);

    Task<Result<MenuCategory>> SaveCategoryAsync(MenuCategory category);

    Task<Result<MenuItem>> SaveItemAsync(MenuItem item);

    /// <summary>
    /// Marks an item available or not ("86'ing" it). Separate from SaveItemAsync because this is
    /// the one menu edit staff make mid-service, from the kitchen display, one tap at a time.
    /// </summary>
    Task<Result<bool>> SetItemAvailabilityAsync(int itemId, bool isAvailable);

    Task<Result<bool>> DeactivateCategoryAsync(int categoryId);

    Task<Result<bool>> DeactivateItemAsync(int itemId);

    /// <summary>Resolves an item's image file id without exposing it to the caller's transport.</summary>
    Task<Result<Guid>> GetItemImageFileIdAsync(int itemId, int expectedCompanyId);

    /// <summary>Which venue a dish belongs to, for authorising a caller against that venue.</summary>
    Task<Result<int>> GetLocationIdForItemAsync(int itemId);

    /// <summary>
    /// The choice groups on one dish, with their options, for the staff editor. Includes
    /// unconfirmed and unavailable choices, which the public menu deliberately hides.
    /// </summary>
    Task<Result<List<MenuItemOptionGroup>>> GetOptionGroupsForItemAsync(int menuItemId);

    Task<Result<MenuItemOptionGroup>> SaveOptionGroupAsync(MenuItemOptionGroup group);

    Task<Result<MenuItemOption>> SaveOptionAsync(MenuItemOption option);

    Task<Result<bool>> SetOptionAvailabilityAsync(int optionId, bool isAvailable);

    Task<Result<bool>> DeactivateOptionGroupAsync(int groupId);

    Task<Result<bool>> DeactivateOptionAsync(int optionId);
}
