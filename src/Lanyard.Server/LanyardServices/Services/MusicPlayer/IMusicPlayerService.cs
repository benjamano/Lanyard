using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using NAudio.Wave;

namespace Lanyard.Application.Services;

/// <summary>
/// Controls music playback on kiosk clients over SignalR and tracks each client's playback state.
/// Every member is keyed by the kiosk's Client.Id.
/// </summary>
public interface IMusicPlayerService
{
    event Action<Guid, PlaybackState>? OnPlaybackStatusChanged;
    event Action<Guid, Guid?>? OnSongChanged;
    event Action<Guid, bool>? OnShuffleStateChanged;
    event Action<Guid>? OnManualMusicChange;

    /// <summary>
    /// Raised whenever anything a now-playing screen shows changes for a kiosk: play state, song,
    /// playlist, a seek or position jump, volume, shuffle, repeat, the queue, or the kiosk
    /// connecting or dropping off. Not raised for the kiosk's routine once-a-second position
    /// reports - a screen moves the position on itself (see <see cref="MusicNowPlayingSnapshot"/>).
    /// </summary>
    event Action<Guid>? OnNowPlayingChanged;

    MusicNowPlayingSnapshot GetSnapshot(Guid clientId);
    IReadOnlyList<MusicNowPlayingSnapshot> GetSnapshots(IEnumerable<Guid> clientIds);

    PlaybackState GetCurrentPlaybackState(Guid clientId);
    Song? GetCurrentSong(Guid clientId);
    Playlist? GetCurrentPlaylist(Guid clientId);
    bool GetShuffleEnabled(Guid clientId);
    double GetEstimatedPositionSeconds(Guid clientId);
    int GetCurrentVolume(Guid clientId);
    /// <summary>False until the kiosk has reported its volume (or one has been set) since the server started.</summary>
    bool IsVolumeKnown(Guid clientId);
    bool GetCurrentShuffleState(Guid clientId);
    RepeatMode GetCurrentRepeatMode(Guid clientId);
    int GetCurrentSongTime(Guid clientId);
    int GetCurrentSongLength(Guid clientId);

    // State reported by the client itself.
    Task<Result<bool>> SetCurrentPlaylist(Guid clientId, Guid playlistId);
    void UpdatePlaybackState(Guid clientId, PlaybackState state);
    void UpdatePlaybackPosition(Guid clientId, double positionSeconds);
    Task UpdateCurrentSongAsync(Guid clientId, Guid songId);
    Task SetQueue(Guid clientId, List<Guid> songIds, Guid? playlistId);
    Task HandleSongEndedAsync(Guid clientId);
    void ReportVolumeFromClient(Guid clientId, int volume);
    void MarkClientConnected(Guid clientId);
    void MarkClientDisconnected(Guid clientId);

    // Queues a playlist to start when the current song ends; null cancels.
    void SetPendingPlaylist(Guid clientId, Guid? playlistId);
    (Guid PlaylistId, DateTime SinceUtc)? GetPendingPlaylist(Guid clientId);

    // Commands sent to the client.
    // Commands that change what plays or how loud take a source; Manual (the default) raises
    // OnManualMusicChange, which the music schedule treats as a change by hand.
    Task<Result<bool>> Play(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual);
    Task<Result<bool>> Play(Guid clientId, Guid playlistId, MusicCommandSource source = MusicCommandSource.Manual);

    /// <summary>
    /// Plays one song. With a playlist the rest of that playlist follows it (in playlist order, or
    /// shuffled); with none the song plays on its own - the All Songs view.
    /// </summary>
    Task<Result<bool>> PlaySong(Guid clientId, Guid songId, Guid? playlistId, MusicCommandSource source = MusicCommandSource.Manual);

    /// <summary>Plays a playlist from the top with shuffle switched on.</summary>
    Task<Result<bool>> ShufflePlay(Guid clientId, Guid playlistId, MusicCommandSource source = MusicCommandSource.Manual);

    Task<Result<bool>> Pause(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual);
    Task<Result<bool>> TogglePlay(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual);
    Task<Result<bool>> Stop(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual);
    Task<Result<bool>> Next(Guid clientId);
    Task<Result<bool>> Previous(Guid clientId);
    Task<Result<bool>> Restart(Guid clientId);

    /// <summary>Restarts the song if it's more than a few seconds in, otherwise goes back one.</summary>
    Task<Result<bool>> RestartOrPrevious(Guid clientId);
    Task<bool> ToggleShuffle(Guid clientId);
    Task<RepeatMode> CycleRepeatMode(Guid clientId);
    Task<Result<bool>> Seek(Guid clientId, double positionSeconds);
    Task GetCachedSongsAsync(Guid clientId);
    Task<Result<bool>> SetVolume(Guid clientId, int volume, MusicCommandSource source = MusicCommandSource.Manual);
}
