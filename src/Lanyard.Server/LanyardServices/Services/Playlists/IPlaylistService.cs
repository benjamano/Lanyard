using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IPlaylistService
{
    Task<Result<IEnumerable<Playlist>>> GetActivePlaylistsAsync();
    Task<Result<IEnumerable<PlaylistSongMember>>> GetPlaylistMembersAsync(Guid playlistId);
    Task<Result<bool>> RemoveSongFromPlaylistAsync(Guid playlistId, Guid songId);
}
