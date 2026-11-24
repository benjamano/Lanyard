using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Lanyard.Application.Services.Checklists;

/// <summary>
/// Raises an operations alert (and its push to the location's Managers and the company's Admins)
/// when a location checklist is missed: past its due time with tasks left, or, with no due time,
/// its venue day over with tasks left. Once per run, claimed with ChecklistRun.MissedAlertUtc so two
/// sweeps can't both raise it. The alert clears itself if the checklist is finished late.
///
/// Sweeps every five minutes as the unfiltered system tenant, so every query names its location.
/// It makes today's and yesterday's runs first, so a day nobody opened the checklists is still
/// noticed. Older days are history only: switching the feature on never raises a flood.
/// </summary>
public class ChecklistMissedAlertHostedService(
    ISystemDbContextFactory contextFactory,
    IOperationsAlertWriter alertWriter,
    TimeProvider timeProvider,
    ILogger<ChecklistMissedAlertHostedService> logger) : BackgroundService
{
    public const string KeyPrefix = "checklist-missed:";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly CultureInfo Uk = new("en-GB");

    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IOperationsAlertWriter _alertWriter = alertWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChecklistMissedAlertHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChecklistMissedAlertHostedService started");

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
                    _logger.LogError(ex, "Missed checklist sweep failed");
                }

                await Task.Delay(SweepInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("ChecklistMissedAlertHostedService stopped");
    }

    private sealed record Candidate(Guid RunId, int CompanyId, int LocationId, string Name, DateOnly VenueDate, DateTime? DueUtc, int Done, int Total);

    /// <summary>One pass. Public so tests can run it without the timer. Returns alerts raised and cleared.</summary>
    public async Task<(int Raised, int Cleared)> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        DateOnly today = RotaTime.Today(now);
        DateOnly yesterday = today.AddDays(-1);

        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        List<Location> locations = await LiveLocationsAsync(ctx, cancellationToken);

        foreach (Location location in locations)
        {
            await ChecklistRunStore.EnsureRunsAsync(ctx, location, yesterday, now, cancellationToken);
            await ChecklistRunStore.EnsureRunsAsync(ctx, location, today, now, cancellationToken);
        }

        List<int> locationIds = [.. locations.Select(x => x.Id)];

        List<Candidate> unalerted = locationIds.Count == 0
            ? []
            : await ctx.ChecklistRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => locationIds.Contains(x.LocationId) && x.MissedAlertUtc == null && (x.VenueDate == today || x.VenueDate == yesterday))
                .Select(x => new Candidate(x.Id, x.CompanyId, x.LocationId, x.Name, x.VenueDate, x.DueUtc,
                    x.Items.Count(i => i.Completions.Any(c => c.IsActive)), x.Items.Count))
                .ToListAsync(cancellationToken);

        int raised = 0;

        foreach (Candidate run in unalerted.Where(x => ChecklistRules.Status(x.Done, x.Total, x.DueUtc, x.VenueDate, now) == ChecklistRunStatus.Missed))
        {
            if (!await ClaimAsync(run.RunId, now, cancellationToken))
            {
                continue;
            }

            Result<OperationsAlertRaiseOutcome> result = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                run.CompanyId, run.LocationId, OperationsAlertType.ChecklistMissed, OperationsAlertSeverity.Warning,
                $"{KeyPrefix}{run.RunId:N}", $"{run.Name} not finished", Detail(run),
                $"/manage/checklists/today?location={run.LocationId}", NotifyTopic: NotificationTopic.ChecklistMissed), cancellationToken);

            if (!result.IsSuccess)
            {
                // Hand the claim back, or this run would never be alerted: the next sweep tries again.
                _logger.LogWarning("Couldn't raise a missed checklist alert for run {RunId}, will retry: {Error}", run.RunId, result.Error);
                await ReleaseClaimAsync(run.RunId, now, cancellationToken);
            }
            else if (result.Data == OperationsAlertRaiseOutcome.Created)
            {
                raised++;
            }
        }

        int cleared = await ClearFinishedAsync(ctx, now, cancellationToken);

        return (raised, cleared);
    }

    // Active locations of active, non-demo companies with checklists (and shift management) on.
    private static async Task<List<Location>> LiveLocationsAsync(ApplicationDbContext ctx, CancellationToken cancellationToken)
    {
        List<int> activeCompanyIds = await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && !x.IsDemo)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        HashSet<int> enabledLocationIds = await CompanyFeatureQueries.GetEnabledLocationIdsAsync(ctx, CompanyFeature.Checklists, cancellationToken);
        HashSet<int> schedulingLocationIds = await CompanyFeatureQueries.GetEnabledLocationIdsAsync(ctx, CompanyFeature.StaffScheduling, cancellationToken);
        enabledLocationIds.IntersectWith(schedulingLocationIds);

        return await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && activeCompanyIds.Contains(x.CompanyId) && enabledLocationIds.Contains(x.Id)
                && ctx.Checklists.Any(c => c.LocationId == x.Id && c.IsActive))
            .ToListAsync(cancellationToken);
    }

    // Marks the run as alerted. False when another sweep got there first.
    private async Task<bool> ClaimAsync(Guid runId, DateTime now, CancellationToken cancellationToken)
    {
        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        ChecklistRun? run = await ctx.ChecklistRuns.FirstOrDefaultAsync(x => x.Id == runId && x.MissedAlertUtc == null, cancellationToken);

        if (run is null)
        {
            return false;
        }

        run.MissedAlertUtc = now;

        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    // Undoes ClaimAsync after the alert couldn't be raised. Only this sweep's own claim (same time) is
    // cleared, so a claim another sweep made since stands.
    private async Task ReleaseClaimAsync(Guid runId, DateTime claimedUtc, CancellationToken cancellationToken)
    {
        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

            ChecklistRun? run = await ctx.ChecklistRuns.FirstOrDefaultAsync(x => x.Id == runId && x.MissedAlertUtc == claimedUtc, cancellationToken);

            if (run is null)
            {
                return;
            }

            run.MissedAlertUtc = null;
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Couldn't release the missed checklist claim on run {RunId}; it won't be alerted", runId);
        }
    }

    // A missed checklist finished late no longer needs anyone: its alert clears itself.
    private async Task<int> ClearFinishedAsync(ApplicationDbContext ctx, DateTime now, CancellationToken cancellationToken)
    {
        List<OperationsAlert> open = await ctx.OperationsAlerts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Type == OperationsAlertType.ChecklistMissed && x.Status != OperationsAlertStatus.Resolved)
            .ToListAsync(cancellationToken);

        int cleared = 0;

        foreach (OperationsAlert alert in open)
        {
            if (!Guid.TryParseExact(alert.DedupKey[KeyPrefix.Length..], "N", out Guid runId))
            {
                continue;
            }

            var run = await ctx.ChecklistRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == runId)
                .Select(x => new
                {
                    Total = x.Items.Count,
                    Done = x.Items.Count(i => i.Completions.Any(c => c.IsActive)),
                    LastTick = x.Items.SelectMany(i => i.Completions).Where(c => c.IsActive).Max(c => (DateTime?)c.CompletedUtc)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (run is null || run.Done < run.Total)
            {
                continue;
            }

            string note = run.LastTick is DateTime last
                ? $"Finished late, at {RotaTime.ToVenueLocal(last).ToString("HH:mm", Uk)}."
                : "Finished late.";

            Result<bool> result = await _alertWriter.ResolveAsync(alert.CompanyId, alert.DedupKey, note, cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't clear missed checklist alert {AlertId}: {Error}", alert.Id, result.Error);
            }
            else if (result.Data)
            {
                cleared++;
            }
        }

        return cleared;
    }

    private static string Detail(Candidate run)
    {
        string done = $"{run.Done} of {run.Total} {(run.Total == 1 ? "task" : "tasks")} done";

        return run.DueUtc is DateTime due
            ? $"{done} by {RotaTime.ToVenueLocal(due).ToString("HH:mm", Uk)} on {run.VenueDate.ToString("ddd d MMM", Uk)}."
            : $"{done} by the end of {run.VenueDate.ToString("ddd d MMM", Uk)}.";
    }
}
