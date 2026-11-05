using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

public interface IPlaylistService
{
    /// <summary>
    /// Active playlists without their members, by name. For pickers that only need names.
    /// </summary>
    Task<Result<IEnumerable<Playlist>>> GetActivePlaylistSummariesAsync();

    /// <summary>Active playlists by name, each with how many songs it has and how long they last.</summary>
    Task<Result<IReadOnlyList<PlaylistOverview>>> GetPlaylistOverviewsAsync();

    /// <summary>A playlist's active songs in playlist order.</summary>
    Task<Result<IReadOnlyList<PlaylistTrack>>> GetPlaylistTracksAsync(Guid playlistId);

    Task<Result<IEnumerable<PlaylistSongMember>>> GetPlaylistMembersAsync(Guid playlistId);

    Task<Result<Playlist>> CreatePlaylistAsync(string name, string? description);
    Task<Result<bool>> RenamePlaylistAsync(Guid playlistId, string name, string? description);

    /// <summary>
    /// Deletes a playlist (soft). Refused while a music schedule or an automation rule plays it,
    /// naming what does, so nothing is left pointing at a playlist that's gone.
    /// </summary>
    Task<Result<bool>> DeletePlaylistAsync(Guid playlistId);

    /// <summary>
    /// Adds a library song to the end of a playlist. Returns false when it's already there.
    /// A song that was in the playlist before and removed comes back.
    /// </summary>
    Task<Result<bool>> AddSongToPlaylistAsync(Guid songId, Guid playlistId);
    Task<Result<bool>> RemoveSongFromPlaylistAsync(Guid playlistId, Guid songId);

    /// <summary>Moves a song to <paramref name="newIndex"/> (0 = first) and renumbers the playlist.</summary>
    Task<Result<bool>> MoveSongAsync(Guid playlistId, Guid songId, int newIndex);
}
