using LanyardData.DataAccess;
using LanyardData.Models;
using Microsoft.EntityFrameworkCore;

namespace LanyardAPI.Services;

public class MusicRepository(ApplicationDbContext context)
{
    private readonly ApplicationDbContext _context = context;
    private static readonly Random _rng = new();

    public async Task UpdateSongDuration(Song song, int durationSeconds)
    {
        song.DurationSeconds = durationSeconds;
        _context.Songs.Update(song);
        await _context.SaveChangesAsync();
    }

    public async Task<Playlist> GetPlaylistById(Guid playlistId)
    {
        return await _context.Playlists
            .Where(x => x.Id == playlistId)
            .FirstOrDefaultAsync() 
            ?? throw new InvalidOperationException("Playlist not found!");
    }

    public async Task<List<Song>> GetPlaylistSongsRandomized(Guid playlistId)
    {
        return [.. (await _context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.Song!)
            .ToListAsync())
        .OrderBy(_ => _rng.Next())];
    }

    public async Task<List<string>> GetExistingSongFilePaths()
    {
        return await _context.Songs
            .Select(x => x.FilePath)
            .ToListAsync();
    }
}
