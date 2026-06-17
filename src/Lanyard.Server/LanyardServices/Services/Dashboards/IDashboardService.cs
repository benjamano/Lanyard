using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IDashboardService
{
    Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync();
    Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId);
    Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId);
    Task<Result<bool>> CreateDashboardAsync(Dashboard dashboard);
    Task<Result<bool>> SaveDashboardAsync(Dashboard dashboard);
    Task<Result<DashboardWidget>> SaveWidgetAsync(DashboardWidget widget);
}
