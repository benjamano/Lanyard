using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Text.Json;

public class SongCacheService : ISongCacheService, IDisposable
{
    private readonly ILogger<SongCacheService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly string _metadataFile;
    private Dictionary<Guid, DateTime> _lastAccessed = [];
    private long _cacheLimitBytes = 500L * 1024 * 1024;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public SongCacheService(ILogger<SongCacheService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "LanyardCache");
        _metadataFile = Path.Combine(_cacheDir, "cache-metadata.json");

        Directory.CreateDirectory(_cacheDir);
        LoadMetadata();
    }

    public void UpdateCacheLimit(int cacheLimitMb)
    {
        _cacheLimitBytes = (long)cacheLimitMb * 1024 * 1024;
        _logger.LogInformation("SongCache: Cache limit set to {Mb}MB", cacheLimitMb);
    }

    public async Task<string> GetAudioSourceAsync(Guid songId)
    {
        string cachedPath = GetCachePath(songId);

        if (File.Exists(cachedPath))
        {
            _logger.LogInformation("SongCache: Cache hit for {SongId}", songId);
            UpdateAccessTime(songId);
            return cachedPath;
        }

        _logger.LogInformation("SongCache: Cache miss for {SongId}, downloading", songId);

        string? downloaded = await DownloadToCache(songId);
        return downloaded ?? BuildApiUrl(songId);
    }

    public void PreCacheInBackground(Guid songId)
    {
        if (File.Exists(GetCachePath(songId)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("SongCache: Pre-caching {SongId} in background", songId);
            await DownloadToCache(songId);
        });
    }

    private async Task<string?> DownloadToCache(Guid songId)
    {
        await _downloadLock.WaitAsync();
        try
        {
            string cachedPath = GetCachePath(songId);

            if (File.Exists(cachedPath))
            {
                UpdateAccessTime(songId);
                return cachedPath;
            }

            if (!EnsureSpaceAvailable())
            {
                _logger.LogWarning("SongCache: Insufficient space to cache {SongId}, will stream directly", songId);
                return null;
            }

            string url = BuildApiUrl(songId);
            string tempPath = cachedPath + ".tmp";

            using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (FileStream fs = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(fs);
            }

            File.Move(tempPath, cachedPath);
            UpdateAccessTime(songId);

            _logger.LogInformation("SongCache: Cached {SongId}", songId);
            return cachedPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SongCache: Failed to download {SongId}", songId);
            return null;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private bool EnsureSpaceAvailable()
    {
        const long MinFreeDiskBytes = 50L * 1024 * 1024;

        string? root = Path.GetPathRoot(_cacheDir);
        if (root == null) return false;

        DriveInfo drive = new(root);
        if (drive.AvailableFreeSpace < MinFreeDiskBytes)
        {
            return false;
        }

        if (GetCacheSize() < _cacheLimitBytes)
        {
            return true;
        }

        EvictLru();
        return GetCacheSize() < _cacheLimitBytes;
    }

    private void EvictLru()
    {
        _logger.LogInformation("SongCache: Cache full, evicting least recently accessed songs");

        List<(Guid id, DateTime lastAccessed, string path)> entries = [];

        foreach (string file in Directory.GetFiles(_cacheDir, "*.mp3"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (Guid.TryParse(name, out Guid id))
            {
                DateTime accessed = _lastAccessed.TryGetValue(id, out DateTime t) ? t : DateTime.MinValue;
                entries.Add((id, accessed, file));
            }
        }

        long targetSize = _cacheLimitBytes * 3 / 4;

        foreach ((Guid id, _, string path) in entries.OrderBy(x => x.lastAccessed))
        {
            if (GetCacheSize() <= targetSize) break;

            try
            {
                File.Delete(path);
                _lastAccessed.Remove(id);
                _logger.LogInformation("SongCache: Evicted {SongId}", id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SongCache: Failed to evict {SongId}", id);
            }
        }

        SaveMetadata();
    }

    private long GetCacheSize() =>
        Directory.GetFiles(_cacheDir, "*.mp3").Sum(f => new FileInfo(f).Length);

    private string GetCachePath(Guid songId) =>
        Path.Combine(_cacheDir, $"{songId}.mp3");

    private static string BuildApiUrl(Guid songId)
    {
        string apiUrl = Environment.GetEnvironmentVariable("API_SERVER_URL")!;
        return $"{apiUrl}/music/audio/{songId}";
    }

    private void UpdateAccessTime(Guid songId)
    {
        _lastAccessed[songId] = DateTime.UtcNow;
        SaveMetadata();
    }

    private void LoadMetadata()
    {
        if (!File.Exists(_metadataFile)) return;

        try
        {
            string json = File.ReadAllText(_metadataFile);
            _lastAccessed = JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SongCache: Failed to load metadata, starting fresh");
            _lastAccessed = [];
        }
    }

    private void SaveMetadata()
    {
        try
        {
            File.WriteAllText(_metadataFile, JsonSerializer.Serialize(_lastAccessed));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SongCache: Failed to save metadata");
        }
    }

    public Result<IEnumerable<CachedSongDTO>> GetCachedSongs()
    {
        try
        {
            List<CachedSongDTO> cachedSongs = [];

            foreach (string file in Directory.GetFiles(_cacheDir, "*.mp3"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(name, out Guid id))
                {
                    cachedSongs.Add(new CachedSongDTO { Id = id });
                }
            }

            return Result<IEnumerable<CachedSongDTO>>.Ok(cachedSongs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SongCache: Failed to retrieve cached songs");
            return Result<IEnumerable<CachedSongDTO>>.Fail("Failed to retrieve cached songs");
        }
    }

    public void Dispose()
    {
        _downloadLock.Dispose();
        _httpClient.Dispose();
    }
}
