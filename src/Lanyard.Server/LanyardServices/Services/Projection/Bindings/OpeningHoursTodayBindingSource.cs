using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// "Open today 10:00 – 18:00" (several windows joined with commas), or "Closed today". Nothing - so
/// the fallback shows - for a kiosk with no location, or a location that has never had hours set,
/// since "Closed today" would be a guess there.
/// </summary>
public class OpeningHoursTodayBindingSource(ISystemDbContextFactory factory) : IProjectionBindingSource
{
    private readonly ISystemDbContextFactory _factory = factory;

    public string Key => ProjectionBindingKeys.OpeningHoursToday;

    public async Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken)
    {
        if (context.LocationId is not int locationId)
        {
            return Result<string?>.Ok(null);
        }

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            // The location has to be this company's: the system context has no tenant filter.
            List<LocationOpeningHours> hours = await ctx.LocationOpeningHours
                .AsNoTracking()
                .TagWithCallSite()
                .Where(h => h.LocationId == locationId
                    && h.IsActive
                    && ctx.Locations.Any(l => l.Id == locationId && l.CompanyId == context.CompanyId && l.IsActive))
                .ToListAsync(cancellationToken);

            return Result<string?>.Ok(Describe(hours, RotaTime.ToVenueLocal(context.NowUtc).DayOfWeek));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string?>.Fail($"Couldn't load today's opening hours: {ex.Message}");
        }
    }

    public static string? Describe(IReadOnlyCollection<LocationOpeningHours> hours, DayOfWeek today)
    {
        List<LocationOpeningHours> valid = [.. hours.Where(h => h.IsActive && OpeningHoursRules.IsValid(h.OpensAt, h.ClosesAt))];

        if (valid.Count == 0)
        {
            return null;
        }

        List<string> windows = [.. valid
            .Where(h => h.DayOfWeek == today)
            .OrderBy(h => h.OpensAt)
            .Select(h => $"{h.OpensAt:HH\\:mm} – {h.ClosesAt:HH\\:mm}")];

        return windows.Count == 0 ? "Closed today" : $"Open today {string.Join(", ", windows)}";
    }
}
