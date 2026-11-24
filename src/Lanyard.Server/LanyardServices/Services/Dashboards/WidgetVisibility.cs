using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

// What a dashboard's "Show only when…" conditions need to look up: the kiosks and locations they
// name (with the company each belongs to, so another company's counts as misconfigured) and the
// locations' opening hours.
public sealed record WidgetVisibilityLookups(
    IReadOnlyDictionary<Guid, AutomationClientInfo> Clients,
    IReadOnlyDictionary<int, AutomationLocationInfo> Locations)
{
    public static WidgetVisibilityLookups Empty { get; } = new(new Dictionary<Guid, AutomationClientInfo>(), new Dictionary<int, AutomationLocationInfo>());
}

/// <summary>
/// Whether a widget shows on a live dashboard right now: every one of its conditions must hold, at
/// the venue's local time, for the dashboard's company. Pure, so it's tested at fixed instants.
/// A widget with no conditions always shows; a misconfigured one (another company's kiosk, a
/// removed location, unreadable settings) never does.
/// </summary>
public static class WidgetVisibility
{
    public static bool IsShown(DashboardWidget widget, int companyId, WidgetVisibilityLookups lookups, DateTime nowUtc, Func<Guid, bool> isInGame)
    {
        List<AutomationRuleCondition> conditions = WidgetShowConditions.Parse(widget.ShowConditionsJson);

        if (conditions.Count == 0)
        {
            return true;
        }

        return AutomationConditionEvaluator.Evaluate(
            conditions,
            companyId,
            nowUtc,
            id => lookups.Clients.GetValueOrDefault(id),
            isInGame,
            id => lookups.Locations.GetValueOrDefault(id)).Passed;
    }
}

public interface IWidgetVisibilityService
{
    // Looks up the kiosks and locations the widgets' conditions name. Unfiltered on purpose (kiosk
    // dashboards run signed out); the evaluator checks each belongs to the dashboard's company.
    Task<Result<WidgetVisibilityLookups>> LoadAsync(IEnumerable<DashboardWidget> widgets);

    bool IsShown(DashboardWidget widget, int companyId, WidgetVisibilityLookups lookups, DateTime nowUtc);
}

public class WidgetVisibilityService(
    ISystemDbContextFactory contextFactory,
    ILaserGameStatusStore gameStatus,
    ILogger<WidgetVisibilityService> logger) : IWidgetVisibilityService
{
    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly ILaserGameStatusStore _gameStatus = gameStatus;
    private readonly ILogger<WidgetVisibilityService> _logger = logger;

    public async Task<Result<WidgetVisibilityLookups>> LoadAsync(IEnumerable<DashboardWidget> widgets)
    {
        try
        {
            List<AutomationRuleCondition> conditions = [.. widgets.SelectMany(x => WidgetShowConditions.Parse(x.ShowConditionsJson))];

            List<Guid> clientIds = [.. conditions
                .Where(c => c.ConditionType == AutomationConditionType.ClientGameStatus)
                .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out ClientGameStatusConditionParameters? p) ? p.ClientId : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()];

            List<int> locationIds = [.. conditions
                .Where(c => c.ConditionType == AutomationConditionType.DuringOpeningHours)
                .Select(c => AutomationConditionParameters.TryParse(c.ParametersJson, out OpeningHoursConditionParameters? p) ? p.LocationId : 0)
                .Where(id => id > 0)
                .Distinct()];

            if (clientIds.Count == 0 && locationIds.Count == 0)
            {
                return Result<WidgetVisibilityLookups>.Ok(WidgetVisibilityLookups.Empty);
            }

            await using ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync();

            Dictionary<Guid, AutomationClientInfo> clients = clientIds.Count == 0
                ? []
                : await ctx.Clients
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(c => clientIds.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, c => new AutomationClientInfo(c.CompanyId, c.Name));

            // A deactivated location is left out, so its conditions fail closed.
            Dictionary<int, AutomationLocationInfo> locations = locationIds.Count == 0
                ? []
                : (await ctx.Locations
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(l => locationIds.Contains(l.Id) && l.IsActive)
                    .Select(l => new { l.Id, l.CompanyId, l.Name, Hours = l.OpeningHours.Where(h => h.IsActive).ToList() })
                    .ToListAsync())
                    .ToDictionary(l => l.Id, l => new AutomationLocationInfo(l.CompanyId, l.Name, l.Hours));

            return Result<WidgetVisibilityLookups>.Ok(new WidgetVisibilityLookups(clients, locations));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load what dashboard widget conditions need");
            return Result<WidgetVisibilityLookups>.Fail($"Failed to check widget conditions: {ex.Message}");
        }
    }

    public bool IsShown(DashboardWidget widget, int companyId, WidgetVisibilityLookups lookups, DateTime nowUtc) =>
        WidgetVisibility.IsShown(widget, companyId, lookups, nowUtc, IsInGame);

    // The status store, as the automation engine uses: it's cleared when a kiosk disconnects.
    private bool IsInGame(Guid clientId) =>
        _gameStatus.TryGetStatus(clientId, out LaserGameStatusDTO? status) && status?.Status == GameStatus.InGame;
}
