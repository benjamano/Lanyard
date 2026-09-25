using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IPlaylistService
{
    Task<Result<IEnumerable<Playlist>>> GetActivePlaylistsAsync();

    /// <summary>
    /// Active playlists without their members. Every list/picker only needs names, and
    /// <see cref="GetActivePlaylistsAsync"/> loads every member and song of every playlist.
    /// </summary>
    Task<Result<IEnumerable<Playlist>>> GetActivePlaylistSummariesAsync();
    Task<Result<IEnumerable<PlaylistSongMember>>> GetPlaylistMembersAsync(Guid playlistId);
    Task<Result<bool>> RemoveSongFromPlaylistAsync(Guid playlistId, Guid songId);

    Task<Result<Playlist>> CreatePlaylistAsync(string name, string? description);

    /// <summary>
    /// Adds a song to a playlist. A song that isn't in the database yet (a dev-scanned local
    /// file) is persisted first and queued for BPM analysis. Returns false when the song is
    /// already in the playlist.
    /// </summary>
    Task<Result<bool>> AddSongToPlaylistAsync(Song song, Guid playlistId);
}
