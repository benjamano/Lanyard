using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Insights;

namespace Lanyard.Application.Services.Insights;

public record InsightsExportFile(string FileName, string ContentType, byte[] Content);

public interface IDemandInsightsService
{
    // Games (walk-in and party), players, booked parties and expected children, clocked and rota
    // hours for one location and date range, bucketed by UK venue hour, with over/under-staffing
    // flags that leave party hours out. Admins and Managers only; refuses system callers.
    Task<Result<InsightsSummary>> GetSummaryAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default);

    // The same figures as one CSV row per hour. Counts and hours only - no names, contact
    // details, allergies, pay or money.
    Task<Result<InsightsExportFile>> ExportCsvAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default);
}
