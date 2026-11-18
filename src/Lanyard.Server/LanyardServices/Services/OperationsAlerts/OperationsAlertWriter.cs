using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.OperationsAlerts;

/// <summary>
/// Raises and clears operations alerts for the producers. At most one unresolved alert exists per
/// dedup key in a company: a repeat raise bumps <see cref="OperationsAlert.LastSeenAtUtc"/> and
/// <see cref="OperationsAlert.OccurrenceCount"/> (keeping any acknowledgement), and only a new
/// alert is notified, so a rule failing every minute sends one notification, not sixty an hour.
/// The unique index on unresolved rows settles two producers racing to insert the same alert.
/// </summary>
public class OperationsAlertWriter(
    ISystemDbContextFactory contextFactory,
    INotificationDispatcher notifications,
    IOperationsAlertEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<OperationsAlertWriter> logger) : IOperationsAlertWriter
{
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly IOperationsAlertEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OperationsAlertWriter> _logger = logger;

    public async Task<Result<OperationsAlertRaiseOutcome>> RaiseAsync(OperationsAlertRaise raise, CancellationToken cancellationToken = default)
    {
        try
        {
            if (raise.CompanyId <= 0)
            {
                return Result<OperationsAlertRaiseOutcome>.Fail($"An alert needs its company; got {raise.CompanyId} for {raise.DedupKey}.");
            }

            if (string.IsNullOrWhiteSpace(raise.DedupKey) || string.IsNullOrWhiteSpace(raise.Title))
            {
                return Result<OperationsAlertRaiseOutcome>.Fail("An alert needs a dedup key and a title.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

            bool companyLive = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == raise.CompanyId && x.IsActive && !x.IsDemo, cancellationToken);

            if (!companyLive || !await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, raise.CompanyId, CompanyFeature.OperationsAlerts, cancellationToken))
            {
                return Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Skipped);
            }

            OperationsAlert? existing = await FindUnresolvedAsync(ctx, raise.CompanyId, raise.DedupKey, cancellationToken);

            if (existing != null)
            {
                Refresh(existing, raise, now);
                await ctx.SaveChangesAsync(cancellationToken);
                Publish(existing, OperationsAlertChangeKind.Refreshed);

                return Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Refreshed);
            }

            OperationsAlert alert = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = raise.CompanyId,
                LocationId = raise.LocationId,
                Type = raise.Type,
                Severity = raise.Severity,
                Status = OperationsAlertStatus.Open,
                DedupKey = Truncate(raise.DedupKey, OperationsAlert.MaxDedupKeyLength)!,
                Title = Truncate(raise.Title, OperationsAlert.MaxTitleLength)!,
                Detail = Truncate(raise.Detail, OperationsAlert.MaxDetailLength),
                LinkUrl = Truncate(raise.LinkUrl, OperationsAlert.MaxLinkLength),
                SubjectClientId = raise.SubjectClientId,
                SubjectRuleId = raise.SubjectRuleId,
                SubjectUserId = raise.SubjectUserId,
                RaisedAtUtc = now,
                LastSeenAtUtc = now,
                OccurrenceCount = 1
            };

            ctx.OperationsAlerts.Add(alert);

            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another producer inserted the same alert between our read and our write, and the
                // unique index on unresolved rows refused ours. Theirs stands; count this sighting.
                return await RefreshAfterRaceAsync(raise, now, cancellationToken);
            }

            _logger.LogInformation("Raised {AlertType} alert {AlertId} ({DedupKey}) for company {CompanyId}",
                alert.Type, alert.Id, alert.DedupKey, alert.CompanyId);

            Publish(alert, OperationsAlertChangeKind.Raised);

            if (raise.NotifyTopic is NotificationTopic topic)
            {
                await NotifyAsync(ctx, alert, topic, now, cancellationToken);
            }

            return Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Created);
        }
        catch (Exception ex)
        {
            return Result<OperationsAlertRaiseOutcome>.Fail($"Couldn't raise alert {raise.DedupKey}: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ResolveAsync(int companyId, string dedupKey, string? note = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

            OperationsAlert? alert = await FindUnresolvedAsync(ctx, companyId, dedupKey, cancellationToken);

            if (alert == null)
            {
                return Result<bool>.Ok(false);
            }

            alert.Status = OperationsAlertStatus.Resolved;
            alert.ResolvedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            alert.Resolution = OperationsAlertResolution.Auto;
            alert.ResolutionNote = Truncate(note, OperationsAlert.MaxNoteLength);

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Cleared {AlertType} alert {AlertId} ({DedupKey}) automatically", alert.Type, alert.Id, alert.DedupKey);

            Publish(alert, OperationsAlertChangeKind.Resolved);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't clear alert {dedupKey}: {ex.Message}");
        }
    }

    private static Task<OperationsAlert?> FindUnresolvedAsync(ApplicationDbContext ctx, int companyId, string dedupKey, CancellationToken cancellationToken) =>
        ctx.OperationsAlerts
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.DedupKey == dedupKey && x.Status != OperationsAlertStatus.Resolved, cancellationToken);

    private static void Refresh(OperationsAlert alert, OperationsAlertRaise raise, DateTime now)
    {
        alert.LastSeenAtUtc = now;
        alert.OccurrenceCount++;

        // Severity only climbs while the alert is open (an expiring document that has now expired);
        // the latest wording is kept, as it describes the problem as it is now.
        if (raise.Severity > alert.Severity)
        {
            alert.Severity = raise.Severity;
        }

        alert.Type = raise.Type;
        alert.Title = Truncate(raise.Title, OperationsAlert.MaxTitleLength)!;
        alert.Detail = Truncate(raise.Detail, OperationsAlert.MaxDetailLength) ?? alert.Detail;
    }

    private async Task<Result<OperationsAlertRaiseOutcome>> RefreshAfterRaceAsync(OperationsAlertRaise raise, DateTime now, CancellationToken cancellationToken)
    {
        await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

        OperationsAlert? winner = await FindUnresolvedAsync(ctx, raise.CompanyId, raise.DedupKey, cancellationToken);

        if (winner == null)
        {
            return Result<OperationsAlertRaiseOutcome>.Fail($"Couldn't save alert {raise.DedupKey}.");
        }

        Refresh(winner, raise, now);
        await ctx.SaveChangesAsync(cancellationToken);
        Publish(winner, OperationsAlertChangeKind.Refreshed);

        return Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Refreshed);
    }

    private async Task NotifyAsync(ApplicationDbContext ctx, OperationsAlert alert, NotificationTopic topic, DateTime now, CancellationToken cancellationToken)
    {
        (List<string> admins, List<string> managers) = await OperationsAlertRecipients.GetAsync(ctx, alert.CompanyId, alert.LocationId, now, cancellationToken);

        List<string> everyone = [.. admins, .. managers];

        if (everyone.Count == 0)
        {
            _logger.LogWarning("Nobody to notify about alert {AlertId} ({DedupKey}): no active Admins or Managers", alert.Id, alert.DedupKey);
            return;
        }

        string? locationName = alert.LocationId is int locationId
            ? await ctx.Locations.AsNoTracking().TagWithCallSite().Where(x => x.Id == locationId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        // Read for the message only, never stored on the alert.
        string? subjectName = alert.SubjectUserId is string userId
            ? (await ctx.Users.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == userId, cancellationToken))?.GetName().Trim()
            : null;

        _notifications.Enqueue(everyone, topic, new OperationsAlertPayload(
            alert.LocationId ?? 0, alert.Id, alert.Type, alert.Severity, alert.Title, alert.Detail,
            locationName, string.IsNullOrWhiteSpace(subjectName) ? null : subjectName, OperationsAlertNotices.AlertsPageUrl));

        _logger.LogInformation("Queued {Topic} for alert {AlertId} to {AdminCount} Admins and {ManagerCount} Managers",
            topic, alert.Id, admins.Count, managers.Count);
    }

    private void Publish(OperationsAlert alert, OperationsAlertChangeKind kind) =>
        _eventBus.Publish(new OperationsAlertChanged(alert.CompanyId, alert.LocationId, alert.Id, kind, alert.Severity, alert.Title));

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
