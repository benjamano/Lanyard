using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Approved hours for payroll. Built on ITimeEntryService.GetTimesheetAsync, so the same location
// check applies: an admin can export any location, a manager only the one they signed in at.
public interface ITimesheetExportService
{
    // What an export of this range would hold, for the warning shown before downloading.
    Task<Result<TimesheetExportSummary>> PreviewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to);

    // The CSV itself, built in memory. Nothing is stored and no names are logged.
    Task<Result<TimesheetExportFile>> ExportCsvAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId);
}
