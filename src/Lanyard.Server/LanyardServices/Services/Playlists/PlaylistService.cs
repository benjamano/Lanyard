using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class PlaylistService(IDbContextFactory<ApplicationDbContext> _factory, ISongAnalysisQueue songAnalysisQueue) : IPlaylistService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = _factory;
    private readonly ISongAnalysisQueue _songAnalysisQueue = songAnalysisQueue;

    public async Task<Result<IEnumerable<Playlist>>> GetActivePlaylistsAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<Playlist> playlists = await context.Playlists
                .AsNoTracking()
                .Where(playlist => playlist.DeleteDate == null)
                .Include(x=> x.Members!)
                .ThenInclude(x=> x.Song)
                .ToListAsync();

            return Result<IEnumerable<Playlist>>.Ok(playlists);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Playlist>>.Fail($"An error occurred while retrieving active playlists: {ex.Message}");
        }
    }

    public async Task<Result<IEnumerable<Playlist>>> GetActivePlaylistSummariesAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<Playlist> playlists = await context.Playlists
                .AsNoTracking()
                .TagWithCallSite()
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
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IEnumerable<PlaylistSongMember> members = await context.PlaylistSongMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x=> x.Song)
                .Include(x=> x.Playlist)
                .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null)
                .ToListAsync();

            return Result<IEnumerable<PlaylistSongMember>>.Ok(members);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<PlaylistSongMember>>.Fail($"An error occurred while retrieving the playlist members: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveSongFromPlaylistAsync(Guid playlistId, Guid songId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            PlaylistSongMember? member = await context.PlaylistSongMembers
                .TagWithCallSite()
                .Where(x => x.PlaylistId == playlistId && x.SongId == songId && x.DeleteDate == null)
                .FirstOrDefaultAsync();

            if (member is null)
            {
                return Result<bool>.Fail("Song is not in this playlist.");
            }

            member.DeleteDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while removing the song from the playlist: {ex.Message}");
        }
    }

    public async Task<Result<Playlist>> CreatePlaylistAsync(string name, string? description)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Result<Playlist>.Fail("A playlist name is required.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Playlist playlist = new()
            {
                Name = name,
                Description = description,
                CreateDate = DateTime.UtcNow
            };

            context.Playlists.Add(playlist);
            await context.SaveChangesAsync();

            return Result<Playlist>.Ok(playlist);
        }
        catch (Exception ex)
        {
            return Result<Playlist>.Fail($"An error occurred while creating the playlist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> AddSongToPlaylistAsync(Song song, Guid playlistId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Guid? existingSongId = await context.Songs
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == song.Id)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync();

            Guid songId;

            if (existingSongId is Guid id)
            {
                songId = id;
            }
            else
            {
                Song newSong = new()
                {
                    Id = Guid.NewGuid(),
                    Name = song.Name,
                    AlbumName = song.AlbumName,
                    FilePath = song.FilePath,
                    DurationSeconds = song.DurationSeconds,
                    CreateDate = DateTime.UtcNow,
                    IsDownloaded = true,
                    IsActive = true
                };

                context.Songs.Add(newSong);
                await context.SaveChangesAsync();

                // First time this dev-scanned song is persisted - queue BPM analysis so
                // beat-synced DMX scenes can lock onto it.
                _songAnalysisQueue.Enqueue(newSong.Id);

                songId = newSong.Id;
            }

            bool alreadyInPlaylist = await context.PlaylistSongMembers
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(psm => psm.SongId == songId && psm.PlaylistId == playlistId && psm.DeleteDate == null);

            if (alreadyInPlaylist)
            {
                return Result<bool>.Ok(false);
            }

            context.PlaylistSongMembers.Add(new PlaylistSongMember
            {
                SongId = songId,
                PlaylistId = playlistId,
                CreateDate = DateTime.UtcNow
            });

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"An error occurred while adding the song to the playlist: {ex.Message}");
        }
    }
}
