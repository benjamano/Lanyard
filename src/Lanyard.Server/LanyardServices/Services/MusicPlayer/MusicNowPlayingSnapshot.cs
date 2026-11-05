using Lanyard.Infrastructure.Models;
using NAudio.Wave;

namespace Lanyard.Application.Services;

/// <summary>
/// A song as the player UI needs it - just enough to show, never an EF entity.
/// </summary>
public sealed record MusicTrackInfo(Guid Id, string Name, string AlbumName, double DurationSeconds);

/// <summary>
/// Everything a screen needs to show one kiosk's music in one read, taken under the player's lock
/// so the fields agree with each other.
/// </summary>
/// <remarks>
/// <see cref="PositionSeconds"/> is the position at <see cref="PositionAtUtc"/>. While
/// <see cref="State"/> is Playing a screen should move it on by the time elapsed since then, rather
/// than asking the server again every second - the snapshot only changes when something actually
/// happens (see <see cref="IMusicPlayerService.OnNowPlayingChanged"/>).
/// </remarks>
public sealed record MusicNowPlayingSnapshot(
    Guid ClientId,
    bool IsConnected,
    PlaybackState State,
    MusicTrackInfo? Song,
    Guid? PlaylistId,
    string? PlaylistName,
    double PositionSeconds,
    DateTime PositionAtUtc,
    int Volume,
    bool IsVolumeKnown,
    bool IsShuffleEnabled,
    RepeatMode RepeatMode,
    IReadOnlyList<MusicTrackInfo> UpNext,
    Guid? PendingPlaylistId)
{
    public double LengthSeconds => Song?.DurationSeconds ?? 0;

    public static MusicNowPlayingSnapshot Empty(Guid clientId) => new(
        clientId,
        IsConnected: false,
        PlaybackState.Stopped,
        Song: null,
        PlaylistId: null,
        PlaylistName: null,
        PositionSeconds: 0,
        PositionAtUtc: DateTime.UtcNow,
        Volume: 0,
        IsVolumeKnown: false,
        IsShuffleEnabled: false,
        RepeatMode.All,
        UpNext: [],
        PendingPlaylistId: null);
}
