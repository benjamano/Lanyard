using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Lanyard.Application.Services;

public class MusicService(IDbContextFactory<ApplicationDbContext> factory, IHostEnvironment hostEnvironment) : IMusicService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHostEnvironment _hostEnvironment = hostEnvironment;
    private static readonly string[] _audioExtensions = [".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".wma"];

    public async Task<Result<IEnumerable<Song>>> GetSongsAsync()
    {
        try
        {
            if (_hostEnvironment.IsProduction())
            {
                // BECAUSE WE USE THE DATABASE IN PRODUCTION, WE NEED TO GET THE SONGS FROM THE DATABASE
                // (files themselves live in the Railway bucket; see FileService for the upload path)

                await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

                IEnumerable<Song> songs = await context.Songs
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(song => song.IsActive)
                    .OrderBy(song => song.AlbumName)
                    .ThenBy(song => song.Name)
                    .ToListAsync();

                return Result<IEnumerable<Song>>.Ok(songs);
            }
            else
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

                return Result<IEnumerable<Song>>.Ok(songs);
            }
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Song>>.Fail($"An error occurred while retrieving songs: {ex.Message}");
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
