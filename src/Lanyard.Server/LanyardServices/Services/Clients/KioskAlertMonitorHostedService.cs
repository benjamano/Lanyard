using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lanyard.Application.Services.Clients;

/// <summary>
/// Every minute, works out each kiosk's health with <see cref="KioskHealthEvaluator"/> (the same
/// rules as the Clients page and the Kiosk Health widget) and notifies the location's Managers and
/// the company's Admins when one goes offline, comes back, or stays degraded. Alerts only start
/// during the location's opening hours; "back online" always goes out so nobody is left thinking
/// a kiosk is still down. What has been sent lives on <see cref="ClientHealthSnapshot"/>, claimed
/// before the notification is queued, so overlapping sweeps or a redeploy can't send twice.
/// </summary>
public partial class KioskAlertMonitorHostedService(
    IServiceScopeFactory scopeFactory,
    INotificationDispatcher notifications,
    IOptions<KioskHealthOptions> options,
    TimeProvider timeProvider,
    ILogger<KioskAlertMonitorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private const int MaxReasonsLength = 500;

    private static readonly CultureInfo UkCulture = new("en-GB");

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly KioskHealthOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskAlertMonitorHostedService> _logger = logger;

    // Every kiosk drops off when the server restarts. Until they've had as long to come back as an
    // outage takes to count, nothing is called offline, so a redeploy never pages anyone.
    private readonly DateTime _startedUtc = timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AlertsEnabled)
        {
            _logger.LogInformation("Kiosk alerts are turned off (Kiosks:Health:AlertsEnabled)");
            return;
        }

        _logger.LogInformation("KioskAlertMonitorHostedService started");

        using PeriodicTimer timer = new(SweepInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    ISystemDbContextFactory factory = scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>();

                    await RunSweepAsync(factory, SignalRControlHub.ConnectedIds, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Kiosk alert sweep failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("KioskAlertMonitorHostedService stopped");
    }

    /// <summary>
    /// One pass over every kiosk with telemetry. Public so tests can run it without the timer, with
    /// their own set of connected hub connections. Returns how many notifications were queued.
    /// </summary>
    public async Task<int> RunSweepAsync(IDbContextFactory<ApplicationDbContext> factory, IReadOnlyCollection<string> connectedIds, CancellationToken cancellationToken = default)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        bool inStartupGrace = now - _startedUtc < _options.OfflineAfter;
        HashSet<string> connected = [.. connectedIds];

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

        // Kiosks that have never sent a heartbeat (clients older than 1.0.41) have nothing to judge
        // them by, so only kiosks with a snapshot are watched.
        List<ClientHealthSnapshot> snapshots = await ctx.ClientHealthSnapshots
            .TagWithCallSite()
            .Include(x => x.Client!)
                .ThenInclude(x => x.Location!)
                .ThenInclude(x => x.OpeningHours.Where(h => h.IsActive))
            .Where(x => ctx.Companies.Any(c => c.Id == x.Client!.CompanyId && c.IsActive && !c.IsDemo))
            .ToListAsync(cancellationToken);

        int queued = 0;

        foreach (ClientHealthSnapshot snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Client client = snapshot.Client!;
            bool isConnected = client.MostRecentConnectionId != null && connected.Contains(client.MostRecentConnectionId);

            KioskHealthView health = KioskHealthEvaluator.Evaluate(snapshot, isConnected, client.LastDisconnectDate, now, _options, newestCompanyVersion: null);

            List<Alert> alerts = Decide(snapshot, health, now, inStartupGrace);

            // Save whenever the alert state moved (a problem first seen, a recovery), not only when
            // something is sent. For an alert this is the claim: once saved, a second sweep sees
            // it as sent and won't repeat it.
            if (ctx.ChangeTracker.HasChanges())
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }

            foreach (Alert alert in alerts)
            {
                queued += await QueueAsync(ctx, client, alert, cancellationToken);
            }
        }

        return queued;
    }

    private abstract record Alert(NotificationTopic Topic);

    private sealed record OfflineAlert(DateTime DownSinceUtc, bool ShutDownCleanly, bool OfflineCopyReady) : Alert(NotificationTopic.KioskOffline);

    private sealed record BackOnlineAlert(int DownForMinutes) : Alert(NotificationTopic.KioskOffline);

    private sealed record NeedsAttentionAlert(List<string> Reasons) : Alert(NotificationTopic.KioskNeedsAttention);

    /// <summary>Updates the snapshot's alert state for this sweep and says what to send.</summary>
    private List<Alert> Decide(ClientHealthSnapshot snapshot, KioskHealthView health, DateTime now, bool inStartupGrace)
    {
        List<Alert> alerts = [];
        Client client = snapshot.Client!;
        bool isOpen = IsOpen(client.Location, now);

        (bool isDown, DateTime downSince, bool cleanly) = health.Status switch
        {
            KioskHealthStatus.Offline => (true, snapshot.LastHeartbeatUtc, false),

            // A clean disconnect shows straight away; give it the same time to come back as a
            // kiosk that went silent, so a restart or an update doesn't alert.
            KioskHealthStatus.Disconnected when client.LastDisconnectDate is DateTime disconnectedAt
                && now - disconnectedAt >= _options.OfflineAfter => (true, disconnectedAt, true),

            _ => (false, default, false)
        };

        bool isUp = health.Status is KioskHealthStatus.Healthy or KioskHealthStatus.Degraded;

        if (isDown && snapshot.OfflineAlertedSinceUtc == null && isOpen && !inStartupGrace)
        {
            snapshot.OfflineAlertedSinceUtc = downSince;
            alerts.Add(new OfflineAlert(downSince, cleanly, snapshot.OfflineFallbackReady == true));
        }
        else if (isUp && snapshot.OfflineAlertedSinceUtc is DateTime downSinceUtc)
        {
            snapshot.OfflineAlertedSinceUtc = null;

            // The heartbeat that brought it back is the end of the outage.
            TimeSpan downFor = snapshot.LastHeartbeatUtc - downSinceUtc;
            alerts.Add(new BackOnlineAlert(Math.Max(1, (int)Math.Round(downFor.TotalMinutes))));
        }

        if (health.Status == KioskHealthStatus.Degraded)
        {
            snapshot.DegradedSinceUtc ??= now;

            HashSet<string> alerted = ParseReasons(snapshot.AttentionAlertReasons);
            List<string> current = [.. health.Reasons];
            bool hasNewReason = current.Any(x => !alerted.Contains(ReasonKey(x)));

            if (hasNewReason && now - snapshot.DegradedSinceUtc >= _options.NeedsAttentionAfter && isOpen)
            {
                snapshot.AttentionAlertReasons = JoinReasons(current.Select(ReasonKey).Union(alerted));
                alerts.Add(new NeedsAttentionAlert(current));
            }
        }
        else if (health.Status == KioskHealthStatus.Healthy)
        {
            // Unresponsive or offline says nothing about the problems, so they're only forgotten
            // once the kiosk reports itself healthy.
            snapshot.DegradedSinceUtc = null;
            snapshot.AttentionAlertReasons = null;
        }

        return alerts;
    }

    // A kiosk with no location, or a location without opening hours set up, is treated as always
    // open: there's nothing to say when nobody would care.
    private static bool IsOpen(Location? location, DateTime now)
    {
        if (location == null || location.OpeningHours.Count == 0)
        {
            return true;
        }

        return OpeningHoursRules.IsOpen(location.OpeningHours, RotaTime.ToVenueLocal(now));
    }

    private async Task<int> QueueAsync(ApplicationDbContext ctx, Client client, Alert alert, CancellationToken cancellationToken)
    {
        (List<string> admins, List<string> managers) = await GetRecipientsAsync(ctx, client, cancellationToken);

        if (admins.Count == 0 && managers.Count == 0)
        {
            _logger.LogWarning("Nobody to tell that kiosk {ClientId} {Topic}: no active Admins or Managers", client.Id, alert.Topic);
            return 0;
        }

        string? locationName = client.Location?.Name;
        int locationId = client.LocationId ?? 0;

        if (admins.Count > 0)
        {
            _notifications.Enqueue(admins, alert.Topic, Payload(alert, client, locationId, locationName, KioskNotices.AdminUrl));
        }

        if (managers.Count > 0)
        {
            _notifications.Enqueue(managers, alert.Topic, Payload(alert, client, locationId, locationName, KioskNotices.ManagerUrl));
        }

        _logger.LogInformation("Queued a {AlertType} alert for kiosk {ClientId} to {AdminCount} Admins and {ManagerCount} Managers",
            alert.GetType().Name, client.Id, admins.Count, managers.Count);

        return admins.Count + managers.Count;
    }

    private NotificationPayload Payload(Alert alert, Client client, int locationId, string? locationName, string url) => alert switch
    {
        OfflineAlert offline => new KioskOfflinePayload(locationId, client.Id, client.Name, locationName,
            FormatSince(offline.DownSinceUtc), offline.ShutDownCleanly, offline.OfflineCopyReady, url),

        BackOnlineAlert back => new KioskBackOnlinePayload(locationId, client.Id, client.Name, locationName, back.DownForMinutes, url),

        NeedsAttentionAlert attention => new KioskNeedsAttentionPayload(locationId, client.Id, client.Name, locationName, attention.Reasons, url),

        _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.GetType().Name, "Unknown kiosk alert.")
    };

    /// <summary>
    /// Admins of the kiosk's company (a member of any of its locations), and the Managers of the
    /// kiosk's own location who aren't also Admins. Locked accounts are left out.
    /// </summary>
    private async Task<(List<string> Admins, List<string> Managers)> GetRecipientsAsync(ApplicationDbContext ctx, Client client, CancellationToken cancellationToken)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        List<string> admins = await (
                from membership in ctx.UserLocationMemberships
                join location in ctx.Locations on membership.LocationId equals location.Id
                join userRole in ctx.UserRoles on membership.UserId equals userRole.UserId
                join role in ctx.Roles on userRole.RoleId equals role.Id
                where location.CompanyId == client.CompanyId
                    && role.IsActive
                    && role.Name == SchedulingRecipients.AdminRole
                    && membership.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId
                select membership.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .Distinct()
            .ToListAsync(cancellationToken);

        List<string> managers = client.LocationId is int locationId
            ? (await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId)).Except(admins).ToList()
            : [];

        List<string> everyone = [.. admins, .. managers];

        HashSet<string> locked = [.. await ctx.Users
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => everyone.Contains(x.Id) && x.LockoutEnd != null && x.LockoutEnd > now)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)];

        return (admins.Where(x => !locked.Contains(x)).ToList(), managers.Where(x => !locked.Contains(x)).ToList());
    }

    // "14:02", or "Thu 16 Oct 14:02" when it wasn't today, in venue time.
    private string FormatSince(DateTime utc)
    {
        DateTime local = RotaTime.ToVenueLocal(utc);
        DateTime today = RotaTime.ToVenueLocal(_timeProvider.GetUtcNow().UtcDateTime);

        return local.Date == today.Date
            ? local.ToString("HH:mm", UkCulture)
            : local.ToString("ddd d MMM HH:mm", UkCulture);
    }

    // Reasons carry live numbers ("High CPU (95%)"), which change every heartbeat. The part before
    // the brackets names the problem, so that is what decides whether it has been alerted already.
    public static string ReasonKey(string reason) => TrailingDetail().Replace(reason, string.Empty).Trim();

    private static HashSet<string> ParseReasons(string? stored) =>
        string.IsNullOrEmpty(stored) ? [] : [.. stored.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static string JoinReasons(IEnumerable<string> keys)
    {
        string joined = string.Join('\n', keys.Distinct());

        return joined.Length <= MaxReasonsLength ? joined : joined[..MaxReasonsLength];
    }

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingDetail();
}
