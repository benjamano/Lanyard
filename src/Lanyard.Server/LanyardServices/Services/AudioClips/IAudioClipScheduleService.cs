using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.AudioClips;

/// <summary>A schedule with its next few plays (UK time), for the Schedules list.</summary>
/// <param name="LocationHasHours">False when a closing or opening time can't work out yet.</param>
public sealed record AudioClipScheduleSummary(AudioClipSchedule Schedule, IReadOnlyList<DateTime> NextPlaysLocal, bool LocationHasHours);

/// <summary>
/// Audio clip schedules: play a clip at a location at a set time, or before closing or after
/// opening. An Admin manages every location's; a Manager their own location's.
/// </summary>
public interface IAudioClipScheduleService
{
    Task<Result<List<AudioClipScheduleSummary>>> ListAsync(LocationScope scope);

    /// <summary>Adds the schedule when its Id is empty, otherwise updates it.</summary>
    Task<Result<AudioClipSchedule>> SaveAsync(LocationScope scope, AudioClipSchedule schedule, string userId);

    Task<Result<bool>> SetEnabledAsync(LocationScope scope, Guid scheduleId, bool isEnabled);

    Task<Result<bool>> DeleteAsync(LocationScope scope, Guid scheduleId);

    /// <summary>Whether the location has any opening hours set, for the dialog's warning.</summary>
    Task<Result<bool>> LocationHasHoursAsync(int locationId);
}
