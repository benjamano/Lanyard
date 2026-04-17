using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class MusicService(IDbContextFactory<ApplicationDbContext> factory) : IMusicService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    
    private static readonly string[] _audioExtensions = [".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma"];

    public Task<Result<IEnumerable<Song>>> GetSongsAsync()
    {
        try
        {
            string musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            IEnumerable<Song> songs = Directory
                .EnumerateFiles(musicFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => _audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f =>
                {
                    double duration = 0;
                    try
                    {
                        using TagLib.File tag = TagLib.File.Create(f);
                        duration = tag.Properties.Duration.TotalSeconds;
                    }
                    catch { }

                    return new Song
                    {
                        Id = Guid.NewGuid(),
                        Name = Path.GetFileNameWithoutExtension(f),
                        AlbumName = Path.GetFileName(Path.GetDirectoryName(f)) ?? string.Empty,
                        FilePath = f,
                        DurationSeconds = duration,
                        CreateDate = File.GetCreationTime(f),
                        IsDownloaded = true,
                        IsActive = true
                    };
                });

            return Task.FromResult(Result<IEnumerable<Song>>.Ok(songs));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<IEnumerable<Song>>.Fail($"An error occurred while retrieving songs: {ex.Message}"));
        }
    }

    public async Task<Result<Song>> GetSongAsync(Guid songId)
    {
        try
        {
            ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Song? song = await context.Songs.FirstOrDefaultAsync(s => s.Id == songId);

            if (song == null)
            {
                return Result<Song>.Fail("Song not found.");
            }

            return Result<Song>.Ok(song);
        }
        catch (Exception ex)
        {
            return Result<Song>.Fail($"An error occurred while retrieving the song: {ex.Message}");
        }
    }
}
