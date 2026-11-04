using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.MusicScheduling;

public sealed record MusicScheduleKiosk(Guid ClientId, string Name);

/// <summary>
/// Reads and edits each location's music schedule. Every method takes the caller's scope: an
/// Admin can work on any active location of their company, a Manager only on the location they
/// signed in at.
/// </summary>
public interface IMusicScheduleService
{
    /// <summary>
    /// The location's schedule with its active dayparts (and their playlists). A location that has
    /// never been saved gets an empty, disabled schedule with no Id.
    /// </summary>
    Task<Result<MusicSchedule>> GetScheduleAsync(LocationScope scope, int locationId);

    Task<Result<bool>> SetEnabledAsync(LocationScope scope, int locationId, bool isEnabled, string actingUserId);

    /// <summary>
    /// Creates (Id empty) or updates a daypart, rejecting it if it overlaps another of the
    /// location's dayparts or plays a playlist the company doesn't have.
    /// </summary>
    Task<Result<MusicDaypart>> SaveDaypartAsync(LocationScope scope, int locationId, MusicDaypart daypart, string actingUserId);

    Task<Result<bool>> DeleteDaypartAsync(LocationScope scope, int locationId, Guid daypartId, string actingUserId);

    /// <summary>
    /// The kiosks set to this location, which are the ones the schedule controls.
    /// </summary>
    Task<Result<List<MusicScheduleKiosk>>> GetLocationKiosksAsync(LocationScope scope, int locationId);

    /// <summary>
    /// Names of enabled automation rules with a music action aimed at one of this location's
    /// kiosks, since those can change the music too.
    /// </summary>
    Task<Result<List<string>>> GetConflictingAutomationRulesAsync(LocationScope scope, int locationId);

    /// <summary>
    /// Active song count per active playlist, so the editor can warn about a playlist with
    /// nothing in it.
    /// </summary>
    Task<Result<Dictionary<Guid, int>>> GetPlaylistSongCountsAsync();
}
