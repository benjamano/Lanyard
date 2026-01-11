using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class PlaylistService(IDbContextFactory<ApplicationDbContext> _factory) : IPlaylistService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = _factory;

    public async Task<Result<IEnumerable<Playlist>>> GetActivePlaylistsAsync()
    {
        try
        {
            ApplicationDbContext context = _factory.CreateDbContext();

            IEnumerable<Playlist> playlists = await context.Playlists
                .AsNoTracking()
                .Where(playlist => playlist.DeleteDate == null)
                .ToListAsync();

            return Result<IEnumerable<Playlist>>.Ok(playlists);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Playlist>>.Fail($"An error occurred while retrieving active playlists: {ex.Message}");
        }
    }

    public async Task<Result<IEnumerable<PlaylistSongMember>>> GetPlaylistMembersAsync(Guid playlistId)
    {
        try
        {
            ApplicationDbContext context = _factory.CreateDbContext();

            IEnumerable<PlaylistSongMember> members = await context.PlaylistSongMembers
                .AsNoTracking()
                .Include(x=> x.Song)
                .Include(x=> x.Playlist)
                .Where(x => x.PlaylistId == playlistId)
                .ToListAsync();

            return Result<IEnumerable<PlaylistSongMember>>.Ok(members);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<PlaylistSongMember>>.Fail($"An error occurred while retrieving the playlist members: {ex.Message}");
        }
    }
}
