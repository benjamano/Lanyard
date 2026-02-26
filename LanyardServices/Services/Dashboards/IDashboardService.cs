using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IDashboardService
{
    Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync();
    Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId);
    Task<Result<Dashboard>> GetDashboardForRenderAsync(Guid dashboardId);
    Task<Result<Dashboard>> SaveDashboardAsync(Dashboard dashboard);
    Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId);
}
