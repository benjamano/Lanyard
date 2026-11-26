using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// Holidays for the opening-hours consumers outside the rota: automation, kiosk alerts, audio clips,
// screens, Insights and What's on. Many run on the system context (no tenant filter), so locations
// are always filtered by id here. Holidays only count at a location with Staff Scheduling switched
// on: nobody can see or change them where it's off, so they mustn't quietly shut the venue.
public static class RotaHolidayQueries
{
    // Active holidays touching from..to, by location. A location with none (or without the feature)
    // has no entry; use For() to read one.
    public static async Task<Dictionary<int, List<RotaHoliday>>> ForLocationsAsync(
        ApplicationDbContext ctx,
        IReadOnlyCollection<int> locationIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (locationIds.Count == 0 || to < from)
        {
            return [];
        }

        List<int> ids = [.. locationIds.Distinct()];

        List<int> enabled = await ctx.LocationFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.LocationId) && x.Feature == CompanyFeature.StaffScheduling && x.IsEnabled)
            .Select(x => x.LocationId)
            .ToListAsync(cancellationToken);

        if (enabled.Count == 0)
        {
            return [];
        }

        List<RotaHoliday> holidays = await ctx.RotaHolidays
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => enabled.Contains(x.LocationId) && x.IsActive && x.StartDate <= to && x.EndDate >= from)
            .OrderBy(x => x.StartDate)
            .ToListAsync(cancellationToken);

        return holidays.GroupBy(x => x.LocationId).ToDictionary(g => g.Key, g => g.ToList());
    }

    // One location's holidays from a ForLocationsAsync result, or none.
    public static IReadOnlyList<RotaHoliday> For(this Dictionary<int, List<RotaHoliday>> byLocation, int? locationId) =>
        locationId is int id && byLocation.TryGetValue(id, out List<RotaHoliday>? list) ? list : [];

    public static Task<Dictionary<int, List<RotaHoliday>>> ForLocationAsync(
        ApplicationDbContext ctx,
        int locationId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        ForLocationsAsync(ctx, [locationId], from, to, cancellationToken);
}
