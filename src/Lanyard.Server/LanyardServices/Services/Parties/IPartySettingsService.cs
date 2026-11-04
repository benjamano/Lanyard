using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Parties;

// A location's party timings and menu. Same access rule as bookings: Admin, or a Manager at
// that location.
public interface IPartySettingsService
{
    // The saved settings, or the defaults (unsaved, Id empty) when the location has none yet.
    Task<Result<PartyLocationSettings>> GetSettingsAsync(LocationScope scope, int locationId);

    Task<Result<PartyLocationSettings>> SaveSettingsAsync(LocationScope scope, PartyLocationSettings settings, string actingUserId);

    // Active items for the location, by kind then sort order.
    Task<Result<List<PartyMenuItem>>> GetMenuAsync(LocationScope scope, int locationId);

    // Creates the item when Id is empty (added at the end of its kind), otherwise updates its text.
    Task<Result<PartyMenuItem>> SaveMenuItemAsync(LocationScope scope, PartyMenuItem item, string actingUserId);

    // Soft delete: the item stops printing on new sheets.
    Task<Result<bool>> RemoveMenuItemAsync(LocationScope scope, Guid itemId, string actingUserId);

    // Swaps the item with its neighbour of the same kind; direction -1 = up, 1 = down.
    Task<Result<bool>> MoveMenuItemAsync(LocationScope scope, Guid itemId, int direction);
}
