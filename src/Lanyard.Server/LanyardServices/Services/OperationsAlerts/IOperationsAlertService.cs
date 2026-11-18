using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.OperationsAlerts;

namespace Lanyard.Application.Services.OperationsAlerts;

// The person looking at alerts: their scope, the company they signed in to and their user id.
// Pages keep it to filter live updates with OperationsAlertAccess.CanSee.
public record OperationsAlertViewer(LocationScope Scope, int CompanyId, string UserId)
{
    public bool CanSee(OperationsAlertChanged change) =>
        OperationsAlertAccess.CanSee(Scope, CompanyId, change.CompanyId, change.LocationId);
}

// The people side of operations alerts: the page, the widget and the nav badge. Works out the
// caller itself and refuses anyone who isn't a signed-in Admin or Manager, because the widget
// can sit on a kiosk dashboard, which runs signed out as the unfiltered system tenant.
public interface IOperationsAlertService
{
    Task<Result<OperationsAlertViewer>> GetViewerAsync();

    // Open and acknowledged alerts, most severe first, then newest.
    Task<Result<List<OperationsAlertView>>> GetOpenAsync(OperationsAlertQuery query, CancellationToken cancellationToken = default);

    // Resolved alerts from the last HistoryDays, newest first.
    Task<Result<OperationsAlertHistoryPage>> GetHistoryAsync(OperationsAlertQuery query, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<Result<OperationsAlertCounts>> GetCountsAsync(CancellationToken cancellationToken = default);

    Task<Result<bool>> AcknowledgeAsync(Guid alertId);

    Task<Result<bool>> ResolveAsync(Guid alertId, string? note);
}
