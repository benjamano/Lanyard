using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

public interface ISongCacheService
{
    /// <summary>
    /// Updates the maximum disk space the cache may use.
    /// </summary>
    void UpdateCacheLimit(int cacheLimitMb);

    /// <summary>
    /// Returns a local file path if the song is cached, otherwise downloads it first.
    /// Falls back to the API URL if caching is not possible (no space).
    /// </summary>
    Task<string> GetAudioSourceAsync(Guid songId);

    /// <summary>
    /// Starts a background download of a song without blocking.
    /// No-op if the song is already cached.
    /// </summary>
    void PreCacheInBackground(Guid songId);

    Result<IEnumerable<CachedSongDTO>> GetCachedSongs();
}
