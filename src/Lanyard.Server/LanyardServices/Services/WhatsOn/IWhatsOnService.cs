using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.WhatsOn;

namespace Lanyard.Application.Services.WhatsOn;

// The What's on calendar: everything every source has on at a location, day by day, with totals
// and who's on the published rota. Admins may look at any location, Managers at their own.
public interface IWhatsOnService
{
    Task<Result<WhatsOnRange>> GetRangeAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeCancelled = false);
}
