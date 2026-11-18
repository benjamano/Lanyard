using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Parties;

// A party type in use at a location: how many bookings use it, and whether it's hidden from the
// booking form's suggestions.
public sealed record PartyTypeUsage(string Type, int Count, bool Hidden);

// Party bookings are per location. Every method takes the caller's LocationScope and refuses
// anything outside it: an Admin may manage any location, a Manager only the one they signed in
// under - the same rule as the rota.
public interface IPartyBookingService
{
    // Parties starting on any venue-local day from..to inclusive, earliest first, with the host loaded.
    Task<Result<List<PartyBooking>>> GetBookingsAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeCancelled = false);

    Task<Result<PartyBooking>> GetBookingAsync(LocationScope scope, Guid bookingId);

    // Creates the booking when Id is empty, otherwise updates it. Payment dates and status are
    // left alone on update - they change through the methods below so a stale form can't undo them.
    Task<Result<PartyBooking>> SaveBookingAsync(LocationScope scope, PartyBooking booking, string actingUserId);

    Task<Result<PartyBooking>> SetCancelledAsync(LocationScope scope, Guid bookingId, bool cancelled, string actingUserId);

    Task<Result<PartyBooking>> SetDepositPaidAsync(LocationScope scope, Guid bookingId, bool paid, string actingUserId);

    Task<Result<PartyBooking>> SetBalancePaidAsync(LocationScope scope, Guid bookingId, bool paid, string actingUserId);

    // Party types already used at the location, most-used first, for the form's suggestions. Types
    // the manager has hidden are left out.
    Task<Result<List<string>>> GetPartyTypeSuggestionsAsync(LocationScope scope, int locationId);

    // Every party type in use at the location (most-used first) with its booking count and whether
    // it's hidden - for the Party Settings screen where types are shown or hidden.
    Task<Result<List<PartyTypeUsage>>> GetPartyTypesAsync(LocationScope scope, int locationId);

    // Shows or hides a party type on the booking form's suggestions. Hiding leaves it on any
    // bookings that already use it.
    Task<Result<bool>> SetPartyTypeHiddenAsync(LocationScope scope, int locationId, string type, bool hidden, string actingUserId);
}
