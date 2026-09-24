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
}
