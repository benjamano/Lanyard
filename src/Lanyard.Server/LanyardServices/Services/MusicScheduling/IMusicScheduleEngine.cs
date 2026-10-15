namespace Lanyard.Application.Services.MusicScheduling;

/// <summary>
/// Applies each location's music schedule to its connected kiosks. Driven by
/// MusicScheduleHostedService; state lives here, so it's a singleton.
/// </summary>
public interface IMusicScheduleEngine
{
    /// <summary>
    /// Works out what every scheduled kiosk should be playing at <paramref name="utcNow"/> and
    /// changes any that haven't had their current part of the day applied yet.
    /// </summary>
    Task TickAsync(DateTime utcNow, CancellationToken ct);
}
