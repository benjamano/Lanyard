using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.WhatsOn;

namespace Lanyard.Application.Services.WhatsOn;

// The What's on calendar: everything every source has on at a location, day by day, with totals
// and who's on the published rota. Admins may look at any location, Managers at their own.
public interface IWhatsOnService
{
    Task<Result<WhatsOnRange>> GetRangeAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeCancelled = false);

    // One day for a dashboard's What's on today widget, shown to anyone who can see the dashboard,
    // including signed-out kiosk screens. Checked against the dashboard's company, not the viewer.
    Task<Result<WhatsOnDay>> GetDashboardDayAsync(int companyId, int locationId, DateOnly day);
}
