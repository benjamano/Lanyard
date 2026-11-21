using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Lanyard.Application.Services.OperationsAlerts;

/// <summary>
/// Hourly, raises an alert for every training assignment past its due date and every staff
/// document due for renewal (Info) or expired (Warning), and clears the ones that no longer apply:
/// training completed or removed, a document renewed, replaced or removed.
///
/// These are for managers. The existing reminder emails to the staff themselves
/// (TrainingDueSoonHostedService, StaffDocumentExpiryReminderHostedService) are unchanged.
///
/// The first sweep after this ships finds every existing overdue course at once, so instead of a
/// notification per alert, each location's Admins and Managers get one summary per topic per sweep.
/// Staff documents have no location of their own; their alerts go to the location the person
/// belongs to, so that location's Managers see them.
/// </summary>
public class ComplianceAlertSweepHostedService(
    ISystemDbContextFactory contextFactory,
    IOperationsAlertWriter alertWriter,
    INotificationDispatcher notifications,
    TimeProvider timeProvider,
    ILogger<ComplianceAlertSweepHostedService> logger) : BackgroundService
{
    public const string TrainingKeyPrefix = "training-overdue:";
    public const string DocumentKeyPrefix = "staff-doc:";
    public const string ComplianceUrl = "/manage/compliance";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    // Let the app finish starting before the first, possibly large, sweep.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private static readonly CultureInfo Uk = new("en-GB");

    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IOperationsAlertWriter _alertWriter = alertWriter;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ComplianceAlertSweepHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ComplianceAlertSweepHostedService started");

        try
        {
            await Task.Delay(StartupDelay, _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(SweepInterval, _timeProvider);

            do
            {
                try
                {
                    await RunSweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Compliance alert sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("ComplianceAlertSweepHostedService stopped");
    }

    private sealed record Wanted(int CompanyId, int? LocationId, OperationsAlertType Type, OperationsAlertSeverity Severity,
        string Key, string Title, string Detail, string UserId, NotificationTopic Topic);

    /// <summary>One pass. Public so tests can run it without the timer. Returns alerts raised and cleared.</summary>
    public async Task<(int Raised, int Cleared)> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        List<int> companies = await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive && !x.IsDemo)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        List<int> trainingCompanies = [];
        List<int> documentCompanies = [];

        foreach (int companyId in companies)
        {
            if (await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.Training, cancellationToken))
            {
                trainingCompanies.Add(companyId);
            }

            if (await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.Onboarding, cancellationToken))
            {
                documentCompanies.Add(companyId);
            }
        }

        Dictionary<(string UserId, int CompanyId), int> homeLocation = await HomeLocationsAsync(ctx, cancellationToken);

        List<Wanted> wanted =
        [
            .. await OverdueTrainingAsync(ctx, trainingCompanies, homeLocation, now, cancellationToken),
            .. await DocumentsDueAsync(ctx, documentCompanies, homeLocation, now, cancellationToken)
        ];

        List<Wanted> created = [];

        foreach (Wanted alert in wanted)
        {
            Result<OperationsAlertRaiseOutcome> result = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                alert.CompanyId, alert.LocationId, alert.Type, alert.Severity, alert.Key, alert.Title, alert.Detail,
                ComplianceUrl, SubjectUserId: alert.UserId), cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't raise compliance alert {DedupKey}: {Error}", alert.Key, result.Error);
            }
            else if (result.Data == OperationsAlertRaiseOutcome.Created)
            {
                created.Add(alert);
            }
        }

        int cleared = await ClearAsync(ctx, [.. wanted.Select(x => (x.CompanyId, x.Key))], cancellationToken);

        await NotifyAsync(ctx, created, now, cancellationToken);

        return (created.Count, cleared);
    }

    // Each person's location in each company: the earliest membership there. Used for alerts
    // about things that have no location of their own.
    private static async Task<Dictionary<(string UserId, int CompanyId), int>> HomeLocationsAsync(ApplicationDbContext ctx, CancellationToken cancellationToken)
    {
        var memberships = await (
                from membership in ctx.UserLocationMemberships
                join location in ctx.Locations on membership.LocationId equals location.Id
                where location.IsActive
                orderby membership.CreateDate, membership.Id
                select new { membership.UserId, location.CompanyId, membership.LocationId })
            .AsNoTracking()
            .TagWithCallSite()
            .ToListAsync(cancellationToken);

        Dictionary<(string, int), int> result = [];

        foreach (var membership in memberships)
        {
            result.TryAdd((membership.UserId, membership.CompanyId), membership.LocationId);
        }

        return result;
    }

    private static async Task<List<Wanted>> OverdueTrainingAsync(ApplicationDbContext ctx, List<int> companyIds,
        Dictionary<(string UserId, int CompanyId), int> homeLocation, DateTime now, CancellationToken cancellationToken)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        var overdue = await ctx.CourseAssignments
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive
                && x.CompletedDate == null
                && x.DueDate != null
                && x.DueDate < now
                && x.Course!.IsActive
                && companyIds.Contains(x.Course.CompanyId)
                && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .Select(x => new { x.Id, x.UserId, x.LocationId, x.DueDate, CourseName = x.Course!.Name, x.Course.CompanyId })
            .ToListAsync(cancellationToken);

        List<Wanted> result = [];

        foreach (var assignment in overdue)
        {
            // Someone who has left every location of the company isn't anyone's to chase.
            if (!homeLocation.TryGetValue((assignment.UserId, assignment.CompanyId), out int home))
            {
                continue;
            }

            result.Add(new Wanted(assignment.CompanyId, assignment.LocationId ?? home,
                OperationsAlertType.TrainingOverdue, OperationsAlertSeverity.Warning,
                $"{TrainingKeyPrefix}{assignment.Id:N}", $"Training overdue: {assignment.CourseName}",
                $"Was due {Day(assignment.DueDate!.Value)}.", assignment.UserId, NotificationTopic.TrainingOverdue));
        }

        return result;
    }

    // Due for renewal once the uploader's reminder date arrives (Info); expired once the expiry
    // date has passed (Warning). The same document keeps one alert as it moves from one to the other.
    private static async Task<List<Wanted>> DocumentsDueAsync(ApplicationDbContext ctx, List<int> companyIds,
        Dictionary<(string UserId, int CompanyId), int> homeLocation, DateTime now, CancellationToken cancellationToken)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        DateTime today = now.Date;

        var due = await ctx.StaffDocuments
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsActive
                && x.ExpiryDate != null
                && x.StaffDocumentType!.IsActive
                && companyIds.Contains(x.CompanyId)
                && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId
                && (x.ExpiryDate < today || (x.ReminderDate != null && x.ReminderDate <= today)))
            .Select(x => new { x.Id, x.UserId, x.CompanyId, x.ExpiryDate, TypeName = x.StaffDocumentType!.Name })
            .ToListAsync(cancellationToken);

        List<Wanted> result = [];

        foreach (var document in due)
        {
            if (!homeLocation.TryGetValue((document.UserId, document.CompanyId), out int home))
            {
                continue;
            }

            bool expired = document.ExpiryDate!.Value < today;

            result.Add(new Wanted(document.CompanyId, home,
                expired ? OperationsAlertType.StaffDocumentExpired : OperationsAlertType.StaffDocumentExpiring,
                expired ? OperationsAlertSeverity.Warning : OperationsAlertSeverity.Info,
                $"{DocumentKeyPrefix}{document.Id:N}",
                expired ? $"{document.TypeName} expired" : $"{document.TypeName} expiring",
                expired ? $"Expired {Day(document.ExpiryDate.Value)}." : $"Expires {Day(document.ExpiryDate.Value)}.",
                document.UserId, NotificationTopic.StaffDocumentExpiring));
        }

        return result;
    }

    // Every open training or document alert that this sweep no longer wants has cleared: completed,
    // renewed, removed, or the feature switched off.
    private async Task<int> ClearAsync(ApplicationDbContext ctx, HashSet<(int CompanyId, string Key)> stillWanted, CancellationToken cancellationToken)
    {
        List<OperationsAlert> open = await ctx.OperationsAlerts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Status != OperationsAlertStatus.Resolved
                && (x.Type == OperationsAlertType.TrainingOverdue
                    || x.Type == OperationsAlertType.StaffDocumentExpiring
                    || x.Type == OperationsAlertType.StaffDocumentExpired))
            .ToListAsync(cancellationToken);

        int cleared = 0;

        foreach (OperationsAlert alert in open.Where(x => !stillWanted.Contains((x.CompanyId, x.DedupKey))))
        {
            string note = alert.Type == OperationsAlertType.TrainingOverdue
                ? "The training was completed or is no longer assigned."
                : "The document was renewed or removed.";

            Result<bool> result = await _alertWriter.ResolveAsync(alert.CompanyId, alert.DedupKey, note, cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't clear compliance alert {AlertId}: {Error}", alert.Id, result.Error);
            }
            else if (result.Data)
            {
                cleared++;
            }
        }

        return cleared;
    }

    // One notification per location and topic for this sweep's new alerts, naming the person when
    // there is only one (read now, never stored on the alert).
    private async Task NotifyAsync(ApplicationDbContext ctx, List<Wanted> created, DateTime now, CancellationToken cancellationToken)
    {
        foreach (IGrouping<(int CompanyId, int? LocationId, NotificationTopic Topic), Wanted> group in created.GroupBy(x => (x.CompanyId, x.LocationId, x.Topic)))
        {
            (List<string> admins, List<string> managers) = await OperationsAlertRecipients.GetAsync(
                ctx, group.Key.CompanyId, group.Key.LocationId, now, cancellationToken);

            List<string> everyone = [.. admins, .. managers];

            if (everyone.Count == 0)
            {
                continue;
            }

            string? locationName = group.Key.LocationId is int locationId
                ? await ctx.Locations.AsNoTracking().TagWithCallSite().Where(x => x.Id == locationId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken)
                : null;

            List<Wanted> alerts = [.. group];
            Wanted first = alerts[0];
            bool training = group.Key.Topic == NotificationTopic.TrainingOverdue;

            string title;
            string? detail;
            string? subjectName = null;

            if (alerts.Count == 1)
            {
                title = first.Title;
                detail = first.Detail;
                subjectName = (await ctx.Users.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == first.UserId, cancellationToken))?.GetName().Trim();
            }
            else
            {
                int people = alerts.Select(x => x.UserId).Distinct().Count();
                title = training
                    ? $"{alerts.Count} training courses are overdue"
                    : $"{alerts.Count} staff documents need renewing";
                detail = $"{people} {(people == 1 ? "person" : "people")} at {locationName ?? "your company"}. See Compliance for who and what.";
            }

            OperationsAlertSeverity severity = alerts.Max(x => x.Severity);

            _notifications.Enqueue(everyone, group.Key.Topic, new OperationsAlertPayload(
                group.Key.LocationId ?? 0, Guid.NewGuid(), first.Type, severity, title, detail, locationName,
                string.IsNullOrWhiteSpace(subjectName) ? null : subjectName, OperationsAlertNotices.AlertsPageUrl));

            _logger.LogInformation("Queued {Topic} summary of {Count} new alerts for location {LocationId} to {Recipients} people",
                group.Key.Topic, alerts.Count, group.Key.LocationId, everyone.Count);
        }
    }

    private static string Day(DateTime date) => date.ToString("ddd d MMM yyyy", Uk);
}
