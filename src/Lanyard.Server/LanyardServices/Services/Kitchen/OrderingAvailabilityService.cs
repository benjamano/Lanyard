using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Whether a venue is taking orders right now, and if not, when it will be.
/// </summary>
/// <param name="IsOpen">False for any reason at all - switched off, outside hours, venue gone.</param>
/// <param name="Message">What to show the customer. Written for them, not for staff.</param>
/// <param name="NextOpensAtLocal">
/// The next opening time in the venue's own time zone, when there is one. Null when the venue has
/// no timetable or has been switched off by hand, because in neither case can we honestly say
/// when it will be back.
/// </param>
public record OrderingAvailability(bool IsOpen, string Message, DateTime? NextOpensAtLocal)
{
    public static OrderingAvailability Open { get; } = new(true, string.Empty, null);
}

public interface IOrderingAvailabilityService
{
    Task<Result<OrderingAvailability>> GetAsync(int locationId);
}

/// <summary>
/// Reads a venue's manual switch and its weekly timetable together.
///
/// Two separate things deliberately. The timetable is the normal week; the switch is "stop now",
/// for a kitchen that is swamped, short-staffed or has run out. Either one closes the venue, and
/// the switch always wins, so a member of staff can shut ordering off without editing hours.
/// </summary>
public class OrderingAvailabilityService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<OrderingAvailabilityService> logger) : IOrderingAvailabilityService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<OrderingAvailabilityService> _logger = logger;

    /// <summary>
    /// How far ahead to look for the next opening. A week covers every weekly timetable exactly
    /// once, so anything not found within it is a venue with no usable hours at all.
    /// </summary>
    private const int DaysToSearch = 7;

    public async Task<Result<OrderingAvailability>> GetAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(l => l.Id == locationId && l.IsActive)
                .Select(l => new
                {
                    l.OrderingEnabled,
                    l.TimeZoneId,
                    Hours = l.OpeningHours
                        .Select(h => new { h.DayOfWeek, h.OpensAt, h.ClosesAt })
                        .ToList()
                })
                .FirstOrDefaultAsync();

            if (location is null)
            {
                return Result<OrderingAvailability>.Ok(
                    new OrderingAvailability(false, "This venue isn't taking orders.", null));
            }

            // The manual switch wins over any timetable: it exists precisely to close a venue
            // that its own hours say should be open.
            if (!location.OrderingEnabled)
            {
                return Result<OrderingAvailability>.Ok(new OrderingAvailability(
                    false, "We've stopped taking orders from phones for now. Please order at the till.", null));
            }

            // No timetable means the switch is the whole answer. A venue that has not set hours
            // is open whenever it is switched on, rather than never.
            if (location.Hours.Count == 0)
            {
                return Result<OrderingAvailability>.Ok(OrderingAvailability.Open);
            }

            TimeZoneInfo zone = ResolveTimeZone(location.TimeZoneId, locationId);
            DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            TimeOnly timeNow = TimeOnly.FromDateTime(nowLocal);

            bool openNow = location.Hours.Any(h =>
                h.DayOfWeek == nowLocal.DayOfWeek
                && timeNow >= h.OpensAt
                // Exclusive, so an order placed exactly at closing time is refused.
                && timeNow < h.ClosesAt);

            if (openNow)
            {
                return Result<OrderingAvailability>.Ok(OrderingAvailability.Open);
            }

            DateTime? next = FindNextOpening(location.Hours.Select(h => (h.DayOfWeek, h.OpensAt)), nowLocal);

            string message = next is DateTime opens
                ? opens.Date == nowLocal.Date
                    ? $"We're not taking orders just now. Ordering opens again at {opens:HH:mm}."
                    : $"We're not taking orders just now. Ordering opens again {opens:dddd} at {opens:HH:mm}."
                : "We're not taking orders just now.";

            return Result<OrderingAvailability>.Ok(new OrderingAvailability(false, message, next));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to work out whether location {LocationId} is taking orders", locationId);

            // Fails closed. Being unable to tell whether a kitchen is open is not grounds for
            // taking someone's money.
            return Result<OrderingAvailability>.Fail("Couldn't check whether this venue is taking orders.");
        }
    }

    /// <summary>
    /// Walks forward a day at a time rather than doing modular arithmetic on day-of-week, because
    /// adding days to a DateTime is what makes "next Monday" behave across a month or year end.
    /// </summary>
    private static DateTime? FindNextOpening(IEnumerable<(DayOfWeek Day, TimeOnly OpensAt)> hours, DateTime nowLocal)
    {
        List<(DayOfWeek Day, TimeOnly OpensAt)> all = [.. hours];

        for (int offset = 0; offset < DaysToSearch; offset++)
        {
            DateTime day = nowLocal.Date.AddDays(offset);

            IEnumerable<TimeOnly> candidates = all
                .Where(h => h.Day == day.DayOfWeek)
                .Select(h => h.OpensAt)
                // Today only counts if it has not already passed.
                .Where(open => offset > 0 || open > TimeOnly.FromDateTime(nowLocal))
                .OrderBy(open => open);

            foreach (TimeOnly open in candidates)
            {
                return day.Add(open.ToTimeSpan());
            }
        }

        return null;
    }

    private TimeZoneInfo ResolveTimeZone(string? timeZoneId, int locationId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falling back to UTC rather than throwing: a mistyped zone should make the hours
            // wrong by an hour, not take a venue's ordering offline entirely.
            _logger.LogWarning(ex,
                "Location {LocationId} has an unusable time zone {TimeZoneId}; reading its opening hours as UTC",
                locationId, timeZoneId);

            return TimeZoneInfo.Utc;
        }
    }
}
