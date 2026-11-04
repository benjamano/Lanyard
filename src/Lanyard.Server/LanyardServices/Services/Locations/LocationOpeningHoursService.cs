using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Locations;

// Kept apart from CompanyLocationService because saving has to clear the automation engine's rule
// cache ("during opening hours" conditions read these rows), and that service is constructed in a
// lot of places that have nothing to do with automation.
//
// Same access rule as CompanyLocationService: a signed-in user may only touch their own company's
// locations, platform admins any. A null tenant (tests, system callers) is unrestricted.
public class LocationOpeningHoursService(
    IDbContextFactory<ApplicationDbContext> factory,
    AutomationEngineService engineService,
    ILogger<LocationOpeningHoursService> logger,
    ITenantContext? tenant = null) : ILocationOpeningHoursService
{
    private const string NoAccessToLocation = "You do not have access to that location.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly AutomationEngineService _engineService = engineService;
    private readonly ILogger<LocationOpeningHoursService> _logger = logger;
    private readonly ITenantContext? _tenant = tenant;

    private bool CanManageCompany(int companyId) => _tenant is null || _tenant.CanManageCompany(companyId);

    // Monday first, the way the week reads on the rota.
    public static int MondayFirstIndex(DayOfWeek day) => ((int)day + 6) % 7;

    public async Task<Result<List<OpeningHoursWindow>>> GetOpeningHoursAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, locationId);

            if (accessError != null)
            {
                return Result<List<OpeningHoursWindow>>.Fail(accessError);
            }

            // Filters ignored only after the explicit company check above: the tenant filter is the
            // signed-in company, which would hide another company's rows from a platform admin.
            List<OpeningHoursWindow> windows = (await ctx.LocationOpeningHours
                    .IgnoreQueryFilters()
                    .Where(h => h.LocationId == locationId && h.IsActive)
                    .AsNoTracking()
                    .TagWithCallSite()
                    .ToListAsync())
                .OrderBy(h => MondayFirstIndex(h.DayOfWeek))
                .ThenBy(h => h.OpensAt)
                .Select(h => new OpeningHoursWindow(h.DayOfWeek, h.OpensAt, h.ClosesAt))
                .ToList();

            return Result<List<OpeningHoursWindow>>.Ok(windows);
        }
        catch (Exception ex)
        {
            return Result<List<OpeningHoursWindow>>.Fail($"Failed to load opening hours: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SaveOpeningHoursAsync(int locationId, IReadOnlyList<OpeningHoursWindow> windows)
    {
        try
        {
            string? validationError = Validate(windows);

            if (validationError != null)
            {
                return Result<bool>.Fail(validationError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, locationId);

            if (accessError != null)
            {
                return Result<bool>.Fail(accessError);
            }

            List<LocationOpeningHours> existing = await ctx.LocationOpeningHours
                .IgnoreQueryFilters()
                .Where(h => h.LocationId == locationId && h.IsActive)
                .TagWithCallSite()
                .ToListAsync();

            List<OpeningHoursWindow> toAdd = [.. windows.Distinct()];

            foreach (LocationOpeningHours row in existing)
            {
                OpeningHoursWindow current = new(row.DayOfWeek, row.OpensAt, row.ClosesAt);

                // An unchanged window keeps its row; anything else is soft-deleted.
                if (!toAdd.Remove(current))
                {
                    row.IsActive = false;
                }
            }

            foreach (OpeningHoursWindow window in toAdd)
            {
                ctx.LocationOpeningHours.Add(new LocationOpeningHours
                {
                    LocationId = locationId,
                    DayOfWeek = window.DayOfWeek,
                    OpensAt = window.OpensAt,
                    ClosesAt = window.ClosesAt,
                    IsActive = true,
                    CreateDate = DateTime.UtcNow
                });
            }

            await ctx.SaveChangesAsync();

            _engineService.InvalidateRuleCache();

            _logger.LogInformation("Saved opening hours for location {LocationId}: {WindowCount} window(s) on {DayCount} day(s)",
                locationId, windows.Count, windows.Select(w => w.DayOfWeek).Distinct().Count());

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save opening hours for location {LocationId}", locationId);
            return Result<bool>.Fail($"Failed to save opening hours: {ex.Message}");
        }
    }

    /// <summary>Public so the editor dialog can show the same message the save would return.</summary>
    public static string? Validate(IReadOnlyList<OpeningHoursWindow> windows)
    {
        return FindProblem(windows)?.Message;
    }

    /// <summary>
    /// The first problem with a set of windows and the day it's on, so the editor can show the
    /// message next to that day rather than at the bottom of a long list.
    /// </summary>
    public static (DayOfWeek Day, string Message)? FindProblem(IReadOnlyList<OpeningHoursWindow> windows)
    {
        OpeningHoursWindow? sameTimes = windows.FirstOrDefault(w => !OpeningHoursRules.IsValid(w.OpensAt, w.ClosesAt));

        if (sameTimes != null)
        {
            return (sameTimes.DayOfWeek, $"{sameTimes.DayOfWeek}: opening and closing times can't be the same.");
        }

        (DayOfWeek Day, TimeOnly A, TimeOnly B)? overlap = OpeningHoursRules.FindOverlap(windows.Select(w => (w.DayOfWeek, w.OpensAt, w.ClosesAt)));

        return overlap is { } o
            ? (o.Day, $"{o.Day}: the hours starting at {o.A:HH:mm} and {o.B:HH:mm} overlap.")
            : null;
    }

    private async Task<string?> CheckAccessAsync(ApplicationDbContext ctx, int locationId)
    {
        Location? location = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(l => l.Id == locationId && l.IsActive);

        if (location == null)
        {
            return "Location not found.";
        }

        return CanManageCompany(location.CompanyId) ? null : NoAccessToLocation;
    }
}
