using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Insights;

// Demand (games played on the location's kiosks) against staffing (clocked and rota hours) for one
// location, bucketed by UK venue hour. The sums live in InsightsRules; this class only decides who
// may ask and loads the facts.
//
// Same access shape as ComplianceService: the widget that calls this can sit on a kiosk dashboard,
// and a signed-out kiosk is the "system" tenant that sees every company, so system callers are
// refused before any query runs. Aggregates hide a cross-company leak well (there are no names to
// look wrong), so every query is pinned to the caller's company or to a location already checked
// to belong to it, rather than leaning on the global tenant filter.
public class DemandInsightsService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    TimeProvider timeProvider,
    ILogger<DemandInsightsService> logger) : IDemandInsightsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DemandInsightsService> _logger = logger;

    // Shifts are never longer than a day, so a shift that reaches into the range started at most
    // this long before it - keeps the (LocationId, StartUtc) index usable.
    private static readonly TimeSpan LongestShift = TimeSpan.FromHours(24);

    public async Task<Result<InsightsSummary>> GetSummaryAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_tenant.IsSystem)
            {
                _logger.LogWarning("Refused demand insights for a signed-out (system) caller");
                return Result<InsightsSummary>.Fail("Insights are only shown to a signed-in Admin or Manager.");
            }

            if (!scope.IsAdmin && !scope.IsManager)
            {
                return Result<InsightsSummary>.Fail("Only Admins and Managers can see insights.");
            }

            if (_tenant.CompanyId is not int companyId)
            {
                return Result<InsightsSummary>.Fail("Your company couldn't be worked out. Please sign in again.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            DateOnly today = RotaTime.Today(now);

            if (query.From > query.To)
            {
                return Result<InsightsSummary>.Fail("The start date must be on or before the end date.");
            }

            if (query.Days > InsightsQuery.MaxRangeDays)
            {
                return Result<InsightsSummary>.Fail($"Pick a range of at most {InsightsQuery.MaxRangeDays} days.");
            }

            if (query.To > today)
            {
                return Result<InsightsSummary>.Fail("The range can't end in the future.");
            }

            // A Manager is pinned to the location they signed in to, whatever was asked for.
            int? requestedLocationId = scope.IsAdmin ? query.LocationId ?? scope.LocationId : scope.LocationId;

            if (!scope.IsAdmin && requestedLocationId is null)
            {
                return Result<InsightsSummary>.Fail("No location is set for this session. Please sign in again.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<Location> companyLocations = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync(cancellationToken);

            // An Admin with no location of their own and none asked for gets the first one.
            Location? location = requestedLocationId is int id
                ? companyLocations.FirstOrDefault(x => x.Id == id)
                : companyLocations.FirstOrDefault();

            if (location is null)
            {
                _logger.LogWarning("Demand insights asked for location {LocationId}, which isn't an active location of company {CompanyId}",
                    requestedLocationId, companyId);
                return Result<InsightsSummary>.Fail(requestedLocationId is null ? "There are no locations to show." : "That location isn't available.");
            }

            DateTime rangeStartUtc = RotaTime.StartOfDayUtc(query.From);
            DateTime rangeEndUtc = RotaTime.StartOfDayUtc(query.To.AddDays(1));

            // Games reach a location only through the kiosk they were played on.
            List<(Guid Id, int? LocationId)> clients = [.. (await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId)
                .Select(x => new { x.Id, x.LocationId })
                .ToListAsync(cancellationToken))
                .Select(x => (x.Id, x.LocationId))];

            List<Guid> clientIds = [.. clients.Where(x => x.LocationId == location.Id).Select(x => x.Id)];
            int unassignedKioskCount = clients.Count(x => x.LocationId is null);

            // A game is recorded when it ends, so one that started on the last evening of the
            // range can be recorded just after it; InsightsRules buckets by the estimated start.
            DateTime gamesUntilUtc = rangeEndUtc + InsightsRules.MaxGameLength;

            List<GameFact> games = clientIds.Count == 0 ? [] : [.. (await ctx.GameResults
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId
                    && clientIds.Contains(x.ClientId)
                    && x.PlayedAtUtc >= rangeStartUtc
                    && x.PlayedAtUtc < gamesUntilUtc)
                .Select(x => new { x.PlayedAtUtc, x.DurationSeconds, Players = x.PlayerScores.Count })
                .ToListAsync(cancellationToken))
                .Select(x => new GameFact(x.PlayedAtUtc, x.DurationSeconds, x.Players))];

            DateTime entriesFromUtc = rangeStartUtc - TimeEntryService.StaleOpenEntry;

            List<WorkSpan> clocked = [.. (await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == location.Id
                    && x.IsActive
                    && x.ClockInUtc >= entriesFromUtc
                    && x.ClockInUtc < rangeEndUtc
                    && (x.ClockOutUtc == null || x.ClockOutUtc > rangeStartUtc))
                .Select(x => new { x.ClockInUtc, x.ClockOutUtc })
                .ToListAsync(cancellationToken))
                .Select(x => new WorkSpan(x.ClockInUtc,
                    InsightsRules.EffectiveClockOutUtc(x.ClockInUtc, x.ClockOutUtc, now, TimeEntryService.StaleOpenEntry)))];

            DateTime shiftsFromUtc = rangeStartUtc - LongestShift;

            // Published shifts only, at their current times; open (unassigned) shifts still count
            // because they're cover the manager planned for.
            List<WorkSpan> rota = [.. (await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == location.Id
                    && x.IsActive
                    && x.PublishedDateUtc != null
                    && x.StartUtc >= shiftsFromUtc
                    && x.StartUtc < rangeEndUtc
                    && x.EndUtc > rangeStartUtc)
                .Select(x => new { x.StartUtc, x.EndUtc, x.BreakMinutes })
                .ToListAsync(cancellationToken))
                .Select(x => InsightsRules.RotaSpan(x.StartUtc, x.EndUtc, x.BreakMinutes))];

            List<LocationOpeningHours> openingHours = await ctx.LocationOpeningHours
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == location.Id && x.IsActive)
                .ToListAsync(cancellationToken);

            List<MusicDaypart> dayparts = await ctx.MusicDayparts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.MusicSchedule!.LocationId == location.Id && x.IsActive)
                .ToListAsync(cancellationToken);

            InsightsFigures figures = InsightsRules.Calculate(query.From, query.To, games, clocked, rota, openingHours, dayparts);

            return Result<InsightsSummary>.Ok(new InsightsSummary(
                location.Id,
                location.GetDisplayName(),
                query.From,
                query.To,
                clientIds.Count,
                unassignedKioskCount,
                openingHours.Count > 0,
                dayparts.Count > 0,
                figures));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load demand insights for location {LocationId}", query.LocationId);
            return Result<InsightsSummary>.Fail($"Failed to load insights: {ex.Message}");
        }
    }

    public async Task<Result<InsightsExportFile>> ExportCsvAsync(LocationScope scope, InsightsQuery query, CancellationToken cancellationToken = default)
    {
        Result<InsightsSummary> summary = await GetSummaryAsync(scope, query, cancellationToken);

        if (!summary.IsSuccess || summary.Data is null)
        {
            return Result<InsightsExportFile>.Fail(summary.Error ?? "Failed to load insights.");
        }

        try
        {
            _logger.LogInformation("Exported demand insights for location {LocationId} from {From} to {To}",
                summary.Data.LocationId, query.From, query.To);

            return Result<InsightsExportFile>.Ok(new InsightsExportFile(
                InsightsExportBuilder.FileName(summary.Data),
                "text/csv",
                InsightsExportBuilder.Build(summary.Data)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export demand insights for location {LocationId}", summary.Data.LocationId);
            return Result<InsightsExportFile>.Fail($"Failed to export insights: {ex.Message}");
        }
    }
}
