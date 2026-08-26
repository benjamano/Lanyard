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

    /// <summary>
    /// Marks the given song as the one currently loaded/playing, protecting its cached file
    /// from LRU eviction while another song is being downloaded in the background.
    /// </summary>
    void SetActiveSong(Guid songId);

    Result<IEnumerable<CachedSongDTO>> GetCachedSongs();
}
