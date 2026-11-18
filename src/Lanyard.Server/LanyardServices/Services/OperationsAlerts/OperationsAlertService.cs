using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.OperationsAlerts;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.OperationsAlerts;

/// <summary>
/// Reads, acknowledges and resolves operations alerts for a signed-in Admin or Manager. Every query
/// is pinned to the caller's company and, for a Manager, the location they signed in to
/// (<see cref="OperationsAlertAccess"/>), on top of the global company filter.
/// </summary>
public class OperationsAlertService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    ICurrentLocationContext locationContext,
    ICurrentUserAccessor currentUser,
    IOperationsAlertEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<OperationsAlertService> logger) : IOperationsAlertService
{
    // How far back the History tab goes. Resolved alerts are purged at
    // OperationsAlertRetentionHostedService's retention age, which is never shorter than this.
    public const int HistoryDays = 90;

    public const int MaxPageSize = 100;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly ICurrentLocationContext _locationContext = locationContext;
    private readonly ICurrentUserAccessor _currentUser = currentUser;
    private readonly IOperationsAlertEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OperationsAlertService> _logger = logger;

    public async Task<Result<OperationsAlertViewer>> GetViewerAsync()
    {
        try
        {
            if (_tenant.IsSystem)
            {
                _logger.LogWarning("Refused operations alerts to a signed-out (system) caller");
                return Result<OperationsAlertViewer>.Fail("Operations alerts are only shown to a signed-in Admin or Manager.");
            }

            if (_tenant.CompanyId is not int companyId)
            {
                return Result<OperationsAlertViewer>.Fail("Your company couldn't be worked out. Please sign in again.");
            }

            Result<LocationScope> scope = await _locationContext.GetScopeAsync();

            if (!scope.IsSuccess || scope.Data is null)
            {
                return Result<OperationsAlertViewer>.Fail(scope.Error ?? "Your location couldn't be worked out. Please sign in again.");
            }

            if (!OperationsAlertAccess.CanUse(scope.Data))
            {
                return Result<OperationsAlertViewer>.Fail("Only Admins and Managers can see operations alerts.");
            }

            if (!scope.Data.IsAdmin && scope.Data.LocationId is null)
            {
                return Result<OperationsAlertViewer>.Fail("No location is set for this session. Please sign in again.");
            }

            Result<string> userId = await _currentUser.GetCurrentUserIdAsync();

            if (!userId.IsSuccess || string.IsNullOrEmpty(userId.Data))
            {
                return Result<OperationsAlertViewer>.Fail("You need to be signed in to see operations alerts.");
            }

            return Result<OperationsAlertViewer>.Ok(new OperationsAlertViewer(scope.Data, companyId, userId.Data));
        }
        catch (Exception ex)
        {
            return Result<OperationsAlertViewer>.Fail($"Couldn't check who is asking for alerts: {ex.Message}");
        }
    }

    public async Task<Result<List<OperationsAlertView>>> GetOpenAsync(OperationsAlertQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<OperationsAlertViewer> viewer = await GetViewerAsync();

            if (!viewer.IsSuccess || viewer.Data is null)
            {
                return Result<List<OperationsAlertView>>.Fail(viewer.Error ?? "Not allowed.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<OperationsAlertView> alerts = await Project(Filter(Visible(ctx, viewer.Data), query, viewer.Data)
                    .Where(x => x.Status != OperationsAlertStatus.Resolved)
                    .OrderByDescending(x => x.Severity)
                    .ThenByDescending(x => x.RaisedAtUtc))
                .ToListAsync(cancellationToken);

            return Result<List<OperationsAlertView>>.Ok(alerts);
        }
        catch (Exception ex)
        {
            return Result<List<OperationsAlertView>>.Fail($"Couldn't load alerts: {ex.Message}");
        }
    }

    public async Task<Result<OperationsAlertHistoryPage>> GetHistoryAsync(OperationsAlertQuery query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<OperationsAlertViewer> viewer = await GetViewerAsync();

            if (!viewer.IsSuccess || viewer.Data is null)
            {
                return Result<OperationsAlertHistoryPage>.Fail(viewer.Error ?? "Not allowed.");
            }

            page = Math.Max(0, page);
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

            DateTime since = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-HistoryDays);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            IQueryable<OperationsAlert> resolved = Filter(Visible(ctx, viewer.Data), query, viewer.Data)
                .Where(x => x.Status == OperationsAlertStatus.Resolved && x.ResolvedAtUtc >= since);

            int total = await resolved.CountAsync(cancellationToken);

            List<OperationsAlertView> alerts = await Project(resolved
                    .OrderByDescending(x => x.ResolvedAtUtc)
                    .Skip(page * pageSize)
                    .Take(pageSize))
                .ToListAsync(cancellationToken);

            return Result<OperationsAlertHistoryPage>.Ok(new OperationsAlertHistoryPage(alerts, total));
        }
        catch (Exception ex)
        {
            return Result<OperationsAlertHistoryPage>.Fail($"Couldn't load alert history: {ex.Message}");
        }
    }

    public async Task<Result<OperationsAlertCounts>> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Result<OperationsAlertViewer> viewer = await GetViewerAsync();

            if (!viewer.IsSuccess || viewer.Data is null)
            {
                return Result<OperationsAlertCounts>.Fail(viewer.Error ?? "Not allowed.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            var counts = await Visible(ctx, viewer.Data)
                .Where(x => x.Status != OperationsAlertStatus.Resolved)
                .GroupBy(x => new { x.Status, x.Severity })
                .Select(g => new { g.Key.Status, g.Key.Severity, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return Result<OperationsAlertCounts>.Ok(new OperationsAlertCounts(
                counts.Where(x => x.Status == OperationsAlertStatus.Open).Sum(x => x.Count),
                counts.Where(x => x.Status == OperationsAlertStatus.Acknowledged).Sum(x => x.Count),
                counts.Where(x => x.Severity == OperationsAlertSeverity.Critical).Sum(x => x.Count)));
        }
        catch (Exception ex)
        {
            return Result<OperationsAlertCounts>.Fail($"Couldn't count alerts: {ex.Message}");
        }
    }

    public Task<Result<bool>> AcknowledgeAsync(Guid alertId) =>
        ChangeAsync(alertId, "acknowledge", OperationsAlertChangeKind.Acknowledged, (alert, userId, now) =>
        {
            if (alert.Status != OperationsAlertStatus.Open)
            {
                return alert.Status == OperationsAlertStatus.Resolved ? "This alert has already been resolved." : "This alert has already been acknowledged.";
            }

            alert.Status = OperationsAlertStatus.Acknowledged;
            alert.AcknowledgedByUserId = userId;
            alert.AcknowledgedAtUtc = now;
            return null;
        });

    public Task<Result<bool>> ResolveAsync(Guid alertId, string? note) =>
        ChangeAsync(alertId, "resolve", OperationsAlertChangeKind.Resolved, (alert, userId, now) =>
        {
            if (alert.Status == OperationsAlertStatus.Resolved)
            {
                return "This alert has already been resolved.";
            }

            string? trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            if (trimmed?.Length > OperationsAlert.MaxNoteLength)
            {
                return $"Keep the note to {OperationsAlert.MaxNoteLength} characters.";
            }

            alert.Status = OperationsAlertStatus.Resolved;
            alert.ResolvedByUserId = userId;
            alert.ResolvedAtUtc = now;
            alert.Resolution = OperationsAlertResolution.Manual;
            alert.ResolutionNote = trimmed;
            return null;
        });

    // Loads the alert, checks the caller may act on it, applies the change (which returns an error
    // message to refuse) and tells open pages.
    private async Task<Result<bool>> ChangeAsync(Guid alertId, string verb, OperationsAlertChangeKind kind,
        Func<OperationsAlert, string, DateTime, string?> apply)
    {
        try
        {
            Result<OperationsAlertViewer> viewer = await GetViewerAsync();

            if (!viewer.IsSuccess || viewer.Data is null)
            {
                return Result<bool>.Fail(viewer.Error ?? "Not allowed.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            OperationsAlert? alert = await ctx.OperationsAlerts
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == alertId);

            // Someone else's alert reads as missing, so its existence isn't given away.
            if (alert == null || !OperationsAlertAccess.CanSee(viewer.Data.Scope, viewer.Data.CompanyId, alert.CompanyId, alert.LocationId))
            {
                return Result<bool>.Fail("That alert couldn't be found.");
            }

            string? refused = apply(alert, viewer.Data.UserId, _timeProvider.GetUtcNow().UtcDateTime);

            if (refused != null)
            {
                return Result<bool>.Fail(refused);
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} chose to {Verb} alert {AlertId} ({DedupKey})", viewer.Data.UserId, verb, alert.Id, alert.DedupKey);

            _eventBus.Publish(new OperationsAlertChanged(alert.CompanyId, alert.LocationId, alert.Id, kind, alert.Severity, alert.Title));

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<bool>.Fail("This alert changed while you were looking at it. Refresh and try again.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't {verb} the alert: {ex.Message}");
        }
    }

    private static IQueryable<OperationsAlert> Visible(ApplicationDbContext ctx, OperationsAlertViewer viewer)
    {
        IQueryable<OperationsAlert> alerts = ctx.OperationsAlerts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == viewer.CompanyId);

        if (!viewer.Scope.IsAdmin)
        {
            int locationId = viewer.Scope.LocationId!.Value;
            alerts = alerts.Where(x => x.LocationId == locationId);
        }

        return alerts;
    }

    private static IQueryable<OperationsAlert> Filter(IQueryable<OperationsAlert> alerts, OperationsAlertQuery query, OperationsAlertViewer viewer)
    {
        if (query.Type is OperationsAlertType type)
        {
            alerts = alerts.Where(x => x.Type == type);
        }

        if (query.MinimumSeverity is OperationsAlertSeverity minimum)
        {
            alerts = alerts.Where(x => x.Severity >= minimum);
        }

        // A Manager's alerts are already pinned to their location, whatever was asked for; only an
        // Admin can narrow to one.
        if (viewer.Scope.IsAdmin && query.LocationId is int locationId)
        {
            alerts = alerts.Where(x => x.LocationId == locationId);
        }

        if (query.OnlyUnacknowledged)
        {
            alerts = alerts.Where(x => x.Status == OperationsAlertStatus.Open);
        }

        return alerts;
    }

    private static IQueryable<OperationsAlertView> Project(IQueryable<OperationsAlert> alerts) =>
        alerts.Select(x => new OperationsAlertView(
            x.Id,
            x.Type,
            x.Severity,
            x.Status,
            x.Title,
            x.Detail,
            x.SubjectUser == null ? null : (x.SubjectUser.FirstName + " " + x.SubjectUser.LastName).Trim(),
            x.LocationId,
            x.Location == null ? null : x.Location.Name,
            x.LinkUrl,
            x.RaisedAtUtc,
            x.LastSeenAtUtc,
            x.OccurrenceCount,
            x.AcknowledgedByUser == null ? null : (x.AcknowledgedByUser.FirstName + " " + x.AcknowledgedByUser.LastName).Trim(),
            x.AcknowledgedAtUtc,
            x.ResolvedByUser == null ? null : (x.ResolvedByUser.FirstName + " " + x.ResolvedByUser.LastName).Trim(),
            x.ResolvedAtUtc,
            x.Resolution,
            x.ResolutionNote));
}
