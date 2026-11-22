using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Parties;

/// <summary>
/// One booked party's times, as automation and venue screens see it. Only parties that really go
/// ahead are included: booked (not cancelled), not a Just Step hold that's still provisional, and
/// not a private hire (no birthday child, and no eat or laser tag times).
/// </summary>
/// <remarks>
/// Carries the child's name for the venue-screen bindings, which decide for themselves whether
/// the company lets it be shown. Nothing that writes to the database may copy it: automation logs
/// keep <see cref="Id"/> only.
/// </remarks>
public sealed record PartySlot(
    Guid Id,
    int LocationId,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime? EatTimeUtc,
    DateTime? LaserTagTimeUtc,
    string? Room,
    string ChildName,
    int? ChildAgeTurning,
    DateTime CreateDate);

/// <summary>
/// Today's parties at a location, cached for a minute and shared by everything that runs on a
/// timer outside any signed-in user (the automation engine, venue-screen bindings). Singleton, on
/// the system context, so it filters by company itself.
/// </summary>
public interface IPartyScheduleProvider
{
    /// <summary>
    /// Parties at <paramref name="locationId"/> that overlap the venue-local day of
    /// <paramref name="nowUtc"/>, plus any starting in the two hours after the next midnight (a
    /// rule can fire up to 120 minutes ahead). Empty when the location isn't the company's, is
    /// closed, or the company has Parties switched off.
    /// </summary>
    Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Forgets what's cached for a location, so the next read sees a booking just saved.</summary>
    void Invalidate(int locationId);
}
