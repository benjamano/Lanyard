using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.WhatsOn;

namespace Lanyard.Application.Services.WhatsOn;

// One kind of thing that can appear on What's on. Parties are the only source today; ordinary
// bookings will be another class implementing this plus one line in Program.cs. A source returns
// nothing (not a failure) when the company doesn't have the feature it belongs to.
public interface IWhatsOnSource
{
    // Entries starting on any venue-local day from..to inclusive at the location.
    Task<Result<List<WhatsOnItem>>> GetItemsAsync(LocationScope scope, int companyId, int locationId, DateOnly from, DateOnly to, bool includeCancelled);
}
