using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Locations;

/// <summary>
/// One opening window, venue-local. A ClosesAt at or before OpensAt runs past midnight.
/// </summary>
public sealed record OpeningHoursWindow(DayOfWeek DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

public interface ILocationOpeningHoursService
{
    /// <summary>The location's active windows, Monday first, then by opening time.</summary>
    Task<Result<List<OpeningHoursWindow>>> GetOpeningHoursAsync(int locationId);

    /// <summary>
    /// Replaces the whole week with these windows. A day with none is closed. Windows that are
    /// unchanged keep their rows; removed ones are soft-deleted.
    /// </summary>
    Task<Result<bool>> SaveOpeningHoursAsync(int locationId, IReadOnlyList<OpeningHoursWindow> windows);
}
