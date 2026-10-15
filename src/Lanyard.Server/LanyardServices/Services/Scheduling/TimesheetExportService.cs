using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class TimesheetExportService(
    ITimeEntryService timeEntryService,
    ISchedulingSettingsService settingsService,
    ILogger<TimesheetExportService> logger) : ITimesheetExportService
{
    // Long enough for a monthly or quarterly pay run, short enough that one export can't pull a
    // location's whole history.
    public const int MaxRangeDays = 93;

    private readonly ITimeEntryService _timeEntryService = timeEntryService;
    private readonly ISchedulingSettingsService _settingsService = settingsService;
    private readonly ILogger<TimesheetExportService> _logger = logger;

    public async Task<Result<TimesheetExportSummary>> PreviewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to)
    {
        try
        {
            Result<TimesheetExport> export = await BuildAsync(scope, locationId, from, to);

            return export.IsSuccess && export.Data is not null
                ? Result<TimesheetExportSummary>.Ok(export.Data.Summary)
                : Result<TimesheetExportSummary>.Fail(export.Error ?? "Couldn't check the timesheet.");
        }
        catch (Exception ex)
        {
            return Result<TimesheetExportSummary>.Fail($"Couldn't check the timesheet: {ex.Message}");
        }
    }

    public async Task<Result<TimesheetExportFile>> ExportCsvAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId)
    {
        try
        {
            Result<TimesheetExport> export = await BuildAsync(scope, locationId, from, to);

            if (!export.IsSuccess || export.Data is null)
            {
                _logger.LogWarning("Timesheet export by {UserId} for location {LocationId} {From}-{To} refused: {Error}",
                    actingUserId, locationId, from, to, export.Error);

                return Result<TimesheetExportFile>.Fail(export.Error ?? "Couldn't export the timesheet.");
            }

            TimesheetExportSummary summary = export.Data.Summary;
            byte[] content = TimesheetExportBuilder.ToCsv(export.Data);

            // Counts only. The file holds staff names and hours, so none of it goes in the log.
            _logger.LogInformation(
                "Timesheet export by {UserId} for location {LocationId} {From}-{To}: {People} people, {DayRows} day rows, {ExcludedRows} excluded",
                actingUserId, locationId, from, to, summary.People, summary.DayRows, summary.ExcludedCount);

            return Result<TimesheetExportFile>.Ok(new TimesheetExportFile(
                TimesheetExportBuilder.FileName(summary.LocationName, from, to),
                TimesheetExportBuilder.ContentType,
                content,
                summary));
        }
        catch (Exception ex)
        {
            return Result<TimesheetExportFile>.Fail($"Couldn't export the timesheet: {ex.Message}");
        }
    }

    public async Task<Result<PayPeriodPresets>> GetPayPeriodPresetsAsync(int companyId, DateOnly today)
    {
        try
        {
            Result<CompanySchedulingSettings> settings = await _settingsService.GetSettingsAsync(companyId);

            if (!settings.IsSuccess || settings.Data is null)
            {
                return Result<PayPeriodPresets>.Fail(settings.Error ?? "Couldn't load the pay period.");
            }

            return Result<PayPeriodPresets>.Ok(new PayPeriodPresets(
                PayPeriodCalculator.Containing(settings.Data, today),
                PayPeriodCalculator.Previous(settings.Data, today),
                PayPeriodCalculator.Describe(settings.Data),
                PayPeriodCalculator.IsConfigured(settings.Data),
                BreakDeductionPolicy.From(settings.Data)));
        }
        catch (Exception ex)
        {
            return Result<PayPeriodPresets>.Fail($"Couldn't load the pay period: {ex.Message}");
        }
    }

    private async Task<Result<TimesheetExport>> BuildAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return Result<TimesheetExport>.Fail("The end of the range must not be before the start.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            return Result<TimesheetExport>.Fail($"Choose a range of {MaxRangeDays} days or less.");
        }

        // The location check lives in here, so the export can never see more than the timesheet.
        Result<TimesheetView> view = await _timeEntryService.GetTimesheetAsync(scope, locationId, from, to);

        if (!view.IsSuccess || view.Data is null)
        {
            return Result<TimesheetExport>.Fail(view.Error ?? "Couldn't load the timesheet.");
        }

        // The break rule is the company's, and the company is the location's - never the caller's,
        // so an admin exporting another company's site gets that company's rule. If it can't be
        // read the export fails: a wrong paid figure is worse than no file.
        Result<CompanySchedulingSettings> settings = await _settingsService.GetSettingsAsync(view.Data.CompanyId);

        if (!settings.IsSuccess || settings.Data is null)
        {
            return Result<TimesheetExport>.Fail(settings.Error ?? "Couldn't load the rota settings for this location.");
        }

        return Result<TimesheetExport>.Ok(TimesheetExportBuilder.Build(view.Data, BreakDeductionPolicy.From(settings.Data)));
    }
}
