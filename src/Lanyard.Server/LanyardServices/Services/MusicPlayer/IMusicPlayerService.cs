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

    PlaybackState GetCurrentPlaybackState(Guid clientId);
    Song? GetCurrentSong(Guid clientId);
    Playlist? GetCurrentPlaylist(Guid clientId);
    bool GetShuffleEnabled(Guid clientId);
    double GetEstimatedPositionSeconds(Guid clientId);
    int GetCurrentVolume(Guid clientId);
    bool GetCurrentShuffleState(Guid clientId);
    RepeatMode GetCurrentRepeatMode(Guid clientId);
    int GetCurrentSongTime(Guid clientId);
    int GetCurrentSongLength(Guid clientId);

    // State reported by the client itself.
    Task<Result<bool>> SetCurrentPlaylist(Guid clientId, Guid playlistId);
    void UpdatePlaybackState(Guid clientId, PlaybackState state);
    void UpdatePlaybackPosition(Guid clientId, double positionSeconds);
    void UpdateCurrentSong(Guid clientId, Guid songId);
    Task SetQueue(Guid clientId, List<Guid> songIds, Guid? playlistId);
    Task HandleSongEndedAsync(Guid clientId);
    void ReportVolumeFromClient(Guid clientId, int volume);

    // Queues a playlist to start when the current song ends; null cancels.
    void SetPendingPlaylist(Guid clientId, Guid? playlistId);
    (Guid PlaylistId, DateTime SinceUtc)? GetPendingPlaylist(Guid clientId);

    // Commands sent to the client.
    Task<Result<bool>> LoadSongsIntoQueue(Guid clientId, Guid playlistId, Song? song = null);
    Task Play(Guid clientId);
    Task<Result<bool>> Play(Guid clientId, Guid playlistId);
    Task Play(Guid clientId, Song song, Playlist? playlist = null);
    Task Pause(Guid clientId);
    Task TogglePlay(Guid clientId);
    Task Stop(Guid clientId);
    Task Next(Guid clientId);
    Task Previous(Guid clientId);
    Task Restart(Guid clientId);
    Task RestartOrPrevious(Guid clientId);
    Task<bool> ToggleShuffle(Guid clientId);
    Task<RepeatMode> CycleRepeatMode(Guid clientId);
    Task Seek(Guid clientId, double positionSeconds);
    Task GetCachedSongsAsync(Guid clientId);
    Task SetVolume(Guid clientId, int volume);
}
