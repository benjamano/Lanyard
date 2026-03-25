using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

public class MusicService : IMusicService
{
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
}
