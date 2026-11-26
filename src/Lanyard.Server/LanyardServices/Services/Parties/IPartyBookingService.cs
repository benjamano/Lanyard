using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Parties;

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

    // The open "Party staffing out of date" alert for the booking's day at its location, if any (so
    // the editor can offer to rerun Auto-build for that day). Ok(null) when the day is fine.
    Task<Result<PartyStaffingAlertView?>> GetStaffingAlertAsync(LocationScope scope, Guid bookingId);

    // Party types already used at the location, most-used first, for the form's suggestions.
    Task<Result<List<string>>> GetPartyTypeSuggestionsAsync(LocationScope scope, int locationId);

    // Rooms already used at the location, most-used first, tidied so "Room 2" and "room 2 " are one.
    Task<Result<List<string>>> GetRoomSuggestionsAsync(LocationScope scope, int locationId);
}
