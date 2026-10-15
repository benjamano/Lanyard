using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.MusicScheduling;

/// <summary>
/// Where a kiosk is in its location's music schedule, for the "now playing" chip. Times are UTC.
/// </summary>
/// <param name="DaypartName">The part of the day running now, or null in a gap.</param>
/// <param name="IsMusicOff">The running part of the day is "Music off".</param>
/// <param name="NextChangeUtc">When the schedule next starts or ends a part of the day.</param>
/// <param name="OverrideUntilUtc">Set while a change by hand is holding the schedule off.</param>
public sealed record MusicScheduleKioskStatus(string? DaypartName, bool IsMusicOff, DateTime? NextChangeUtc, DateTime? OverrideUntilUtc)
{
    public bool IsOverridden => OverrideUntilUtc is not null;
}

/// <summary>
/// Applies each location's music schedule to its connected kiosks. Driven by
/// MusicScheduleHostedService; state lives here, so it's a singleton.
/// </summary>
public interface IMusicScheduleEngine
{
    /// <summary>
    /// Raised with a kiosk's id whenever its status may have changed: a part of the day was
    /// applied, the music was changed by hand, or the schedule was resumed.
    /// </summary>
    event Action<Guid>? OnStatusChanged;

    /// <summary>
    /// Works out what every scheduled kiosk should be playing at <paramref name="utcNow"/> and
    /// changes any that haven't had their current part of the day applied yet.
    /// </summary>
    Task TickAsync(DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Holds the schedule off a kiosk until its next part of the day starts, after someone
    /// changed its music by hand. Does nothing for a kiosk with no schedule switched on.
    /// </summary>
    Task<Result<bool>> RecordManualChangeAsync(Guid clientId);

    /// <summary>
    /// Ends a change by hand early and puts the kiosk straight back on the schedule.
    /// </summary>
    Task<Result<bool>> ResumeScheduleAsync(Guid clientId);

    /// <summary>
    /// The kiosk's place in its schedule, or null when it doesn't follow a schedule that's on.
    /// </summary>
    Task<Result<MusicScheduleKioskStatus?>> GetStatusAsync(Guid clientId);
}
