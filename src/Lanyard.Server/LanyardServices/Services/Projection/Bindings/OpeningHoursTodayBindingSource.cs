using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// "Open today 10:00 – 18:00" (several windows joined with commas), "Closed today", or "Closed today
/// – Christmas Day" on a day marked Closed under Rota > Holidays. A bank holiday shows the hours of
/// the day it's staffed like. Nothing - so the fallback shows - for a kiosk with no location, or a
/// location that has never had hours set (and isn't closed for a holiday), since "Closed today"
/// would be a guess there.
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
            bool ours = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(l => l.Id == locationId && l.CompanyId == context.CompanyId && l.IsActive, cancellationToken);

            if (!ours)
            {
                return Result<string?>.Ok(null);
            }

            List<LocationOpeningHours> hours = await ctx.LocationOpeningHours
                .AsNoTracking()
                .TagWithCallSite()
                .Where(h => h.LocationId == locationId && h.IsActive)
                .ToListAsync(cancellationToken);

            DateTime venueLocal = RotaTime.ToVenueLocal(context.NowUtc);
            DateOnly today = DateOnly.FromDateTime(venueLocal);
            Dictionary<int, List<RotaHoliday>> holidays = await RotaHolidayQueries.ForLocationAsync(ctx, locationId, today.AddDays(-1), today, cancellationToken);
            IReadOnlyList<RotaHoliday> list = holidays.For(locationId);

            return Result<string?>.Ok(Describe(hours, OpeningHoursRules.Resolve(hours, list, today), OpeningHoursRules.Resolve(hours, list, today.AddDays(-1)), venueLocal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string?>.Fail($"Couldn't load today's opening hours: {ex.Message}");
        }
    }

    public static string? Describe(IReadOnlyCollection<LocationOpeningHours> hours, OpeningDay today, OpeningDay? yesterday = null, DateTime? venueLocal = null)
    {
        if (today.Holiday.Closed is RotaHoliday closed)
        {
            // Still in last night's late hours: say when they end rather than "closed".
            if (venueLocal is DateTime now && yesterday?.Windows.FirstOrDefault(w => w.Contains(now)) is OpeningWindow late)
            {
                return $"Open until {late.ClosesLocal:HH\\:mm}";
            }

            return $"Closed today – {closed.Name}";
        }

        if (!hours.Any(h => h.IsActive && OpeningHoursRules.IsValid(h.OpensAt, h.ClosesAt)))
        {
            return null;
        }

        List<string> windows = [.. today.Windows.Select(w => $"{w.OpensLocal:HH\\:mm} – {w.ClosesLocal:HH\\:mm}")];

        return windows.Count == 0 ? "Closed today" : $"Open today {string.Join(", ", windows)}";
    }
}
