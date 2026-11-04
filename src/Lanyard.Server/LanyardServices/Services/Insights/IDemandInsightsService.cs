using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Insights;

namespace Lanyard.Application.Services.Insights;

public record InsightsExportFile(string FileName, string ContentType, byte[] Content);

public interface IDemandInsightsService
{
    // Games, players, clocked and rota hours for one location and date range, bucketed by UK venue
    // hour, with over/under-staffing flags. Admins and Managers only; refuses system callers.
    Task<Result<InsightsSummary>> GetSummaryAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default);

    // The same figures as one CSV row per hour. Hours only - no names, no pay.
    Task<Result<InsightsExportFile>> ExportCsvAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default);
}
