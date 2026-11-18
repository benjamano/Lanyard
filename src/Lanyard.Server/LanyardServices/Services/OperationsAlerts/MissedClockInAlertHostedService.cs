using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Lanyard.Application.Services.OperationsAlerts;

/// <summary>
/// Raises a missed clock-in alert when a published shift has been running for the company's
/// <see cref="CompanySchedulingSettings.MissedClockInAlertMinutes"/> and the person on it hasn't
/// clocked in at that location. The alert clears when they clock in, or when the shift is removed,
/// unpublished or given to someone else. When a shift ends with nobody clocked in, the alert stays
/// open: it has become a missed shift, which needs a manager.
///
/// Sweeps every five minutes, and straight away after any terminal clock-in so an alert clears
/// as soon as the person turns up. Runs as the unfiltered system tenant, so every query names its
/// company or location.
/// </summary>
public class MissedClockInAlertHostedService(
    ISystemDbContextFactory contextFactory,
    IOperationsAlertWriter alertWriter,
    ITerminalEventBus terminalEvents,
    TimeProvider timeProvider,
    ILogger<MissedClockInAlertHostedService> logger) : BackgroundService
{
    public const string KeyPrefix = "missed-clock-in:";
    public const string TimesheetsUrl = "/manage/rota/timesheets";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // Shifts that started longer ago than this are no longer "running": anything still unclocked
    // by then has had its alert raised long since.
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(24);

    private static readonly CultureInfo Uk = new("en-GB");

    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IOperationsAlertWriter _alertWriter = alertWriter;
    private readonly ITerminalEventBus _terminalEvents = terminalEvents;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<MissedClockInAlertHostedService> _logger = logger;

    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MissedClockInAlertHostedService started");

        _terminalEvents.OnClock += OnClock;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunSweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Missed clock-in sweep failed");
                }

                await Task.WhenAny(Task.Delay(SweepInterval, _timeProvider, stoppingToken), _wake.WaitAsync(stoppingToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _terminalEvents.OnClock -= OnClock;
        }

        _logger.LogInformation("MissedClockInAlertHostedService stopped");
    }

    // A clock-in may clear an alert; run the next sweep now rather than in up to five minutes.
    private void OnClock(TerminalClockEvent clockEvent)
    {
        if (clockEvent.Direction != ClockDirection.In || _wake.CurrentCount > 0)
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sweep is already due.
        }
    }

    private sealed record Candidate(Guid ShiftId, string UserId, int LocationId, int CompanyId, DateTime StartUtc, DateTime EndUtc);

    /// <summary>One pass. Public so tests can run it without the timer. Returns alerts raised and cleared.</summary>
    public async Task<(int Raised, int Cleared)> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        Dictionary<int, CompanySchedulingSettings?> settingsByCompany = await LiveCompanySettingsAsync(ctx, cancellationToken);

        int raised = await RaiseMissedAsync(ctx, settingsByCompany, now, cancellationToken);
        int cleared = await ClearAsync(ctx, settingsByCompany, now, cancellationToken);

        return (raised, cleared);
    }

    // Active, non-demo companies with shift management switched on, and their scheduling settings
    // (null where they have never saved any, meaning the defaults).
    private static async Task<Dictionary<int, CompanySchedulingSettings?>> LiveCompanySettingsAsync(ApplicationDbContext ctx, CancellationToken cancellationToken)
    {
        List<int> companyIds = await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && !x.IsDemo)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        Dictionary<int, CompanySchedulingSettings> saved = await ctx.CompanySchedulingSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => companyIds.Contains(x.CompanyId))
            .ToDictionaryAsync(x => x.CompanyId, cancellationToken);

        Dictionary<int, CompanySchedulingSettings?> result = [];

        foreach (int companyId in companyIds)
        {
            if (await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.StaffScheduling, cancellationToken))
            {
                result[companyId] = saved.GetValueOrDefault(companyId);
            }
        }

        return result;
    }

    private async Task<int> RaiseMissedAsync(ApplicationDbContext ctx, Dictionary<int, CompanySchedulingSettings?> settingsByCompany,
        DateTime now, CancellationToken cancellationToken)
    {
        List<int> companyIds = [.. settingsByCompany.Keys];
        DateTime earliest = now - LookBack;

        // Published, assigned shifts at those companies that have started and haven't ended.
        List<Candidate> running = (await (
                from shift in ctx.Shifts
                join location in ctx.Locations on shift.LocationId equals location.Id
                where shift.IsActive
                    && shift.PublishedDateUtc != null
                    && shift.UserId != null
                    && shift.StartUtc <= now
                    && shift.StartUtc >= earliest
                    && shift.EndUtc > now
                    && companyIds.Contains(location.CompanyId)
                select new { shift, location.CompanyId })
            .AsNoTracking()
            .TagWithCallSite()
            .ToListAsync(cancellationToken))
            .Select(x => ToCandidate(x.shift, x.CompanyId))
            .ToList();

        if (running.Count == 0)
        {
            return 0;
        }

        List<(string UserId, int LocationId, DateTime ClockInUtc)> clockIns = await ClockInsAsync(ctx, running, now, cancellationToken);

        int raised = 0;

        foreach (Candidate shift in running)
        {
            CompanySchedulingSettings? settings = settingsByCompany[shift.CompanyId];
            int grace = settings?.MissedClockInAlertMinutes ?? 15;

            if (grace <= 0 || now < shift.StartUtc.AddMinutes(grace) || now >= shift.EndUtc)
            {
                continue;
            }

            if (HasClockedIn(clockIns, shift, settings))
            {
                continue;
            }

            Result<OperationsAlertRaiseOutcome> result = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                shift.CompanyId, shift.LocationId, OperationsAlertType.MissedClockIn, OperationsAlertSeverity.Warning,
                $"{KeyPrefix}{shift.ShiftId:N}", "Missed clock-in",
                $"Shift {Time(shift.StartUtc)}–{Time(shift.EndUtc)} started with nobody clocked in by {Time(shift.StartUtc.AddMinutes(grace))}.",
                TimesheetsUrl, SubjectUserId: shift.UserId, NotifyTopic: NotificationTopic.MissedClockIn), cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't raise a missed clock-in alert for shift {ShiftId}: {Error}", shift.ShiftId, result.Error);
            }
            else if (result.Data == OperationsAlertRaiseOutcome.Created)
            {
                raised++;
            }
        }

        return raised;
    }

    // Open missed clock-in alerts are cleared once the person clocks in, or the shift is removed,
    // unpublished or reassigned. Nothing else clears them: an ended shift stays for a manager.
    private async Task<int> ClearAsync(ApplicationDbContext ctx, Dictionary<int, CompanySchedulingSettings?> settingsByCompany,
        DateTime now, CancellationToken cancellationToken)
    {
        List<OperationsAlert> open = await ctx.OperationsAlerts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Type == OperationsAlertType.MissedClockIn && x.Status != OperationsAlertStatus.Resolved)
            .ToListAsync(cancellationToken);

        if (open.Count == 0)
        {
            return 0;
        }

        Dictionary<Guid, OperationsAlert> byShift = [];

        foreach (OperationsAlert alert in open)
        {
            if (Guid.TryParseExact(alert.DedupKey[KeyPrefix.Length..], "N", out Guid shiftId))
            {
                byShift[shiftId] = alert;
            }
        }

        List<Guid> shiftIds = [.. byShift.Keys];

        Dictionary<Guid, Shift> shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => shiftIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        List<Candidate> stillAssigned = [];
        int cleared = 0;

        foreach ((Guid shiftId, OperationsAlert alert) in byShift)
        {
            if (!shifts.TryGetValue(shiftId, out Shift? shift) || !shift.IsActive || shift.PublishedDateUtc == null || shift.UserId != alert.SubjectUserId)
            {
                cleared += await ClearOneAsync(alert, "The shift was removed, unpublished or given to someone else.", cancellationToken);
                continue;
            }

            stillAssigned.Add(ToCandidate(shift, alert.CompanyId));
        }

        if (stillAssigned.Count == 0)
        {
            return cleared;
        }

        List<(string UserId, int LocationId, DateTime ClockInUtc)> clockIns = await ClockInsAsync(ctx, stillAssigned, now, cancellationToken);

        foreach (Candidate shift in stillAssigned)
        {
            CompanySchedulingSettings? settings = settingsByCompany.GetValueOrDefault(shift.CompanyId);
            DateTime? clockedInAt = FirstClockIn(clockIns, shift, settings);

            if (clockedInAt is DateTime at)
            {
                cleared += await ClearOneAsync(byShift[shift.ShiftId], $"Clocked in at {Time(at)}.", cancellationToken);
            }
        }

        return cleared;
    }

    private async Task<int> ClearOneAsync(OperationsAlert alert, string note, CancellationToken cancellationToken)
    {
        Result<bool> result = await _alertWriter.ResolveAsync(alert.CompanyId, alert.DedupKey, note, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("Couldn't clear missed clock-in alert {AlertId}: {Error}", alert.Id, result.Error);
            return 0;
        }

        return result.Data ? 1 : 0;
    }

    // A shift edited since it was published still runs to the published times as far as the person
    // on it knows, so those are what "late" is judged by.
    private static Candidate ToCandidate(Shift shift, int companyId)
    {
        bool editedSincePublish = shift.UpdateDate is DateTime updated && shift.PublishedDateUtc is DateTime published && updated > published;

        DateTime start = editedSincePublish && shift.PublishedStartUtc is DateTime publishedStart ? publishedStart : shift.StartUtc;
        DateTime end = editedSincePublish && shift.PublishedEndUtc is DateTime publishedEnd ? publishedEnd : shift.EndUtc;

        return new Candidate(shift.Id, shift.UserId!, shift.LocationId, companyId, start, end);
    }

    private static async Task<List<(string UserId, int LocationId, DateTime ClockInUtc)>> ClockInsAsync(
        ApplicationDbContext ctx, List<Candidate> shifts, DateTime now, CancellationToken cancellationToken)
    {
        List<string> userIds = [.. shifts.Select(x => x.UserId).Distinct()];
        DateTime from = shifts.Min(x => x.StartUtc).AddHours(-4);

        var entries = await ctx.TimeEntries
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && userIds.Contains(x.UserId) && x.ClockInUtc >= from && x.ClockInUtc <= now)
            .Select(x => new { x.UserId, x.LocationId, x.ClockInUtc })
            .ToListAsync(cancellationToken);

        return [.. entries.Select(x => (x.UserId, x.LocationId, x.ClockInUtc))];
    }

    private static bool HasClockedIn(List<(string UserId, int LocationId, DateTime ClockInUtc)> clockIns, Candidate shift, CompanySchedulingSettings? settings) =>
        FirstClockIn(clockIns, shift, settings) is not null;

    // A clock-in at that location from the start of the clock-in window (the earliest a terminal
    // would accept one for this shift) counts; a clock-in at another location doesn't.
    private static DateTime? FirstClockIn(List<(string UserId, int LocationId, DateTime ClockInUtc)> clockIns, Candidate shift, CompanySchedulingSettings? settings)
    {
        DateTime windowStart = shift.StartUtc.AddMinutes(-(settings?.ClockInWindowMinutes ?? 60));

        return clockIns
            .Where(x => x.UserId == shift.UserId && x.LocationId == shift.LocationId && x.ClockInUtc >= windowStart)
            .Select(x => (DateTime?)x.ClockInUtc)
            .Min();
    }

    private static string Time(DateTime utc) => RotaTime.ToVenueLocal(utc).ToString("HH:mm", Uk);

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
