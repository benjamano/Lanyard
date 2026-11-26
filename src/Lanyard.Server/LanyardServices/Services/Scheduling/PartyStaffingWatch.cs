using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// See IPartyStaffingWatch. A day is checked only once its rota is built (it has at least one active
// shift) - before that there's nothing to be out of date, and Auto-build will staff the party when
// the week is built. The judgement itself is RotaAutoBuildRules.CheckPartyStaffing over the same
// demand Auto-build loads (RotaDemandLoader), so the alert and Auto-build always agree.
public sealed class PartyStaffingWatch(
    ISystemDbContextFactory factory,
    IOperationsAlertWriter alertWriter,
    INotificationDispatcher notifications,
    TimeProvider timeProvider,
    ILogger<PartyStaffingWatch> logger) : IPartyStaffingWatch
{
    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IOperationsAlertWriter _alertWriter = alertWriter;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly TimeProvider _time = timeProvider;
    private readonly ILogger<PartyStaffingWatch> _logger = logger;

    public Task<Result<PartyStaffingCheckOutcome>> CheckDaysAsync(int locationId, IReadOnlyCollection<DateOnly> days,
        string? actingUserId = null, CancellationToken cancellationToken = default) =>
        CheckAsync(locationId, days, actingUserId, cancellationToken);

    public Task<Result<PartyStaffingCheckOutcome>> CheckWatchedDaysAsync(int locationId, string? actingUserId = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = RotaTime.Today(_time.GetUtcNow().UtcDateTime);
        List<DateOnly> days = [.. Enumerable.Range(0, IPartyStaffingWatch.HorizonDays + 1).Select(today.AddDays)];

        return CheckAsync(locationId, days, actingUserId, cancellationToken);
    }

    public async Task<Result<int>> RecheckOpenAsync(int? locationId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<(int CompanyId, int LocationId, string DedupKey)> open = (await ctx.OperationsAlerts
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.Type == OperationsAlertType.PartyStaffingOutOfDate && x.Status != OperationsAlertStatus.Resolved
                        && x.LocationId != null && (locationId == null || x.LocationId == locationId))
                    .Select(x => new { x.CompanyId, LocationId = x.LocationId!.Value, x.DedupKey })
                    .ToListAsync(cancellationToken))
                .Select(x => (x.CompanyId, x.LocationId, x.DedupKey))
                .ToList();

            int cleared = 0;

            foreach (IGrouping<(int CompanyId, int LocationId), (int CompanyId, int LocationId, string DedupKey)> group in open.GroupBy(x => (x.CompanyId, x.LocationId)))
            {
                List<DateOnly> days = [.. group.Select(x => PartyStaffingAlerts.TryParseDay(x.DedupKey, out DateOnly day) ? day : (DateOnly?)null)
                    .OfType<DateOnly>()];

                Result<PartyStaffingCheckOutcome> result = await CheckAsync(group.Key.LocationId, days, null, cancellationToken, mayRaise: false);

                if (result.IsSuccess)
                {
                    cleared += result.Data!.Resolved.Count;
                }
            }

            return Result<int>.Ok(cleared);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Party staffing recheck failed for {LocationId}", locationId);
            return Result<int>.Fail($"Failed to recheck party staffing: {ex.Message}");
        }
    }

    private async Task<Result<PartyStaffingCheckOutcome>> CheckAsync(int locationId, IReadOnlyCollection<DateOnly> days,
        string? actingUserId, CancellationToken cancellationToken, bool mayRaise = true)
    {
        try
        {
            if (days.Count == 0)
            {
                return Result<PartyStaffingCheckOutcome>.Ok(PartyStaffingCheckOutcome.Empty);
            }

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            DateOnly today = RotaTime.Today(nowUtc);
            DateOnly last = today.AddDays(IPartyStaffingWatch.HorizonDays);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            // Unfiltered system context: the company comes from the location row, never the tenant.
            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == locationId, cancellationToken);

            if (location is null)
            {
                return Result<PartyStaffingCheckOutcome>.Fail("Location not found.");
            }

            PartyStaffingCheckOutcome outcome = PartyStaffingCheckOutcome.Empty;
            List<DateOnly> distinct = [.. days.Distinct().Order()];

            bool watching = location.IsActive
                && await CompanyFeatureQueries.IsEnabledForLocationAsync(ctx, locationId, CompanyFeature.StaffScheduling, cancellationToken)
                && await CompanyFeatureQueries.IsEnabledForLocationAsync(ctx, locationId, CompanyFeature.Parties, cancellationToken);

            List<DateOnly> watched = watching ? [.. distinct.Where(d => d >= today && d <= last)] : [];

            // Past days (and every day, once the location stops using rota or parties) are no longer
            // anything to act on, so any alert for them is cleared. Days past the horizon are left alone.
            foreach (DateOnly day in distinct.Where(d => d < today || !watching))
            {
                await ResolveAsync(location, day, outcome, "No longer needs checking.", cancellationToken);
            }

            if (watched.Count == 0)
            {
                return Result<PartyStaffingCheckOutcome>.Ok(outcome);
            }

            // Always the whole horizon, whichever days were asked about, so the answer for a day never
            // depends on what else was checked with it: a published shift left on the day a party moved
            // from still stands in for its staff on the new day.
            List<ShiftTemplate> templates = await RotaDemandLoader.LoadTemplatesAsync(ctx, locationId, cancellationToken);
            RotaDemand demand = await RotaDemandLoader.LoadAsync(ctx, locationId, templates, today, last, cancellationToken);

            Dictionary<DateOnly, PartyStaffingDayCheck> checks = RotaAutoBuildRules
                .CheckPartyStaffing(demand.PartyDemand, demand.Parties, demand.Existing, today, last, nowUtc)
                .ToDictionary(x => x.Day);

            if (mayRaise)
            {
                // A party's staff can sit on another day than the party itself (a shift that starts the
                // evening before an after-midnight party), so that day is looked at too.
                HashSet<DateOnly> requested = [.. watched];
                HashSet<Guid> changedParties = [.. demand.Parties.Where(p => requested.Contains(RotaTime.LocalDate(p.StartUtc))).Select(p => p.Id)];

                watched = [.. watched.Concat(checks.Values
                        .Where(c => c.Missing.Any(x => changedParties.Contains(x.PartyBookingId)) || c.Stale.Any(x => changedParties.Contains(x.PartyBookingId)))
                        .Select(c => c.Day))
                    .Distinct()
                    .Order()];
            }

            HashSet<DateOnly> built = [.. demand.Existing.Where(x => x.IsActive).Select(x => RotaTime.LocalDate(x.StartUtc))];

            Dictionary<string, string> names = await NamesAsync(ctx, checks.Values, cancellationToken);
            Dictionary<Guid, string> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == location.CompanyId)
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

            List<(DateOnly Day, string Title, string Detail)> created = [];

            foreach (DateOnly day in watched)
            {
                if (!built.Contains(day) || !checks.TryGetValue(day, out PartyStaffingDayCheck? check))
                {
                    await ResolveAsync(location, day, outcome, "Party staffing matches the bookings again.", cancellationToken);
                    continue;
                }

                string title = $"Party staffing out of date: {day.ToString("ddd d MMM", RotaFormat.Uk)}";
                string detail = Detail(check, names, positions);

                // The recheck only keeps an open alert's text current (a shift named in it may be gone
                // by now); it never creates one, so an alert resolved by hand stays resolved.
                if (!mayRaise)
                {
                    await RefreshIfOpenAsync(ctx, location, day, title, detail, cancellationToken);
                    outcome.StillOpen.Add(day);
                    continue;
                }

                Result<OperationsAlertRaiseOutcome> raised = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                    location.CompanyId, location.Id, OperationsAlertType.PartyStaffingOutOfDate, OperationsAlertSeverity.Warning,
                    PartyStaffingAlerts.DedupKey(location.Id, day), title, detail, PartyStaffingAlerts.RotaLink(location.Id, day)),
                    cancellationToken);

                if (!raised.IsSuccess)
                {
                    _logger.LogWarning("Couldn't raise the party staffing alert for {LocationId} on {Day}: {Error}", location.Id, day, raised.Error);
                    continue;
                }

                switch (raised.Data)
                {
                    case OperationsAlertRaiseOutcome.Created:
                        outcome.Raised.Add(day);
                        created.Add((day, title, detail));
                        break;
                    case OperationsAlertRaiseOutcome.Refreshed:
                        outcome.StillOpen.Add(day);
                        break;
                }
            }

            if (created.Count > 0)
            {
                await NotifyAsync(ctx, location, created, actingUserId, nowUtc, cancellationToken);
            }

            _logger.LogInformation("Party staffing checked {Days} days at {LocationId}: {Raised} raised, {Open} still open, {Resolved} cleared",
                watched.Count, location.Id, outcome.Raised.Count, outcome.StillOpen.Count, outcome.Resolved.Count);

            return Result<PartyStaffingCheckOutcome>.Ok(outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Party staffing check failed at {LocationId}", locationId);
            return Result<PartyStaffingCheckOutcome>.Fail($"Failed to check party staffing: {ex.Message}");
        }
    }

    private async Task RefreshIfOpenAsync(ApplicationDbContext ctx, Location location, DateOnly day, string title, string detail, CancellationToken cancellationToken)
    {
        string key = PartyStaffingAlerts.DedupKey(location.Id, day);

        OperationsAlert? open = await ctx.OperationsAlerts
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.CompanyId == location.CompanyId && x.DedupKey == key && x.Status != OperationsAlertStatus.Resolved, cancellationToken);

        if (open is null || open.Detail == detail)
        {
            return;
        }

        // An open alert with the same key exists, so the writer refreshes it in place.
        await _alertWriter.RaiseAsync(new OperationsAlertRaise(
            location.CompanyId, location.Id, OperationsAlertType.PartyStaffingOutOfDate, OperationsAlertSeverity.Warning,
            key, title, detail, PartyStaffingAlerts.RotaLink(location.Id, day)), cancellationToken);
    }

    private async Task ResolveAsync(Location location, DateOnly day, PartyStaffingCheckOutcome outcome, string note, CancellationToken cancellationToken)
    {
        Result<bool> resolved = await _alertWriter.ResolveAsync(location.CompanyId, PartyStaffingAlerts.DedupKey(location.Id, day), note, cancellationToken);

        if (!resolved.IsSuccess)
        {
            _logger.LogWarning("Couldn't clear the party staffing alert for {LocationId} on {Day}: {Error}", location.Id, day, resolved.Error);
        }
        else if (resolved.Data)
        {
            outcome.Resolved.Add(day);
        }
    }

    private static async Task<Dictionary<string, string>> NamesAsync(ApplicationDbContext ctx, IEnumerable<PartyStaffingDayCheck> checks, CancellationToken cancellationToken)
    {
        List<string> ids = [.. checks.SelectMany(x => x.Stale).Select(x => x.UserId).OfType<string>().Distinct()];

        return ids.Count == 0
            ? []
            : await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => RotaNames.For(u), cancellationToken);
    }

    // Short and without children's names: "Party: Play needs 1 more Host (09:00-14:00). Amy's published
    // Host shift (09:00-14:00) is for a party that was cancelled or no longer needs it - remove it on the rota."
    internal static string Detail(PartyStaffingDayCheck check, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<Guid, string> positions)
    {
        List<string> lines = [];

        foreach (PartyStaffingGap gap in check.Missing)
        {
            lines.Add($"{gap.PartyName} needs {gap.Needed} more {gap.PositionName} ({RotaFormat.TimeRange(gap.StartUtc, gap.EndUtc)}).");
        }

        foreach (StalePartyShift shift in check.Stale)
        {
            string who = shift.UserId is string id ? names.GetValueOrDefault(id, "Someone") + "'s" : "An open";
            string position = shift.StaffPositionId is Guid p ? positions.GetValueOrDefault(p, "party") : "party";
            string times = RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc);

            lines.Add(!shift.IsPublished
                ? $"{who} draft {position} shift ({times}) is for a party that was cancelled or changed. Auto-build takes it off."
                : shift.Moved
                    ? $"{who} published {position} shift ({times}) no longer matches its party's times. Change it on the rota."
                    : $"{who} published {position} shift ({times}) is for a party that was cancelled or no longer needs it. Remove it on the rota if it isn't needed.");
        }

        string detail = string.Join(" ", lines);

        return detail.Length <= OperationsAlert.MaxDetailLength ? detail : detail[..(OperationsAlert.MaxDetailLength - 1)] + "…";
    }

    // One notification per location per check, however many days it raised, so an import bringing in
    // a season of bookings is a single push. The person who made the change isn't sent it.
    private async Task NotifyAsync(ApplicationDbContext ctx, Location location, List<(DateOnly Day, string Title, string Detail)> created,
        string? actingUserId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        (List<string> admins, List<string> managers) = await OperationsAlertRecipients.GetAsync(ctx, location.CompanyId, location.Id, nowUtc, cancellationToken);

        List<string> recipients = [.. admins.Concat(managers).Distinct().Where(x => x != actingUserId)];

        if (recipients.Count == 0)
        {
            return;
        }

        (DateOnly firstDay, string title, string detail) = created[0];
        string url = PartyStaffingAlerts.RotaLink(location.Id, firstDay);

        if (created.Count > 1)
        {
            List<string> labels = [.. created.Select(x => x.Day.ToString("ddd d MMM", RotaFormat.Uk))];
            title = $"Party staffing out of date on {created.Count} days";
            detail = $"Parties changed after the rota was built for {string.Join(", ", labels[..^1])} and {labels[^1]}. Rerun Auto-build for each day from Operations alerts.";
            url = OperationsAlertNotices.AlertsPageUrl;
        }

        _notifications.Enqueue(recipients, NotificationTopic.PartyStaffingOutOfDate, new OperationsAlertPayload(
            location.Id, Guid.NewGuid(), OperationsAlertType.PartyStaffingOutOfDate, OperationsAlertSeverity.Warning,
            title, detail, location.Name, null, url));

        _logger.LogInformation("Queued party staffing summary of {Count} days for {LocationId} to {Recipients} people",
            created.Count, location.Id, recipients.Count);
    }
}
