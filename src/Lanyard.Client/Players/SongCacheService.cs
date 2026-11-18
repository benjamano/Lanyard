using Lanyard.Client.Security;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Music;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Timer = System.Threading.Timer;

public class SongCacheService : ISongCacheService, IDisposable
{
    private readonly ILogger<SongCacheService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly string _metadataFile;
    private ConcurrentDictionary<Guid, DateTime> _lastAccessed = new();
    private long _cacheLimitBytes = 500L * 1024 * 1024;
    private Guid? _activeSongId;

    // One download per song, shared by everyone who wants it (see SharedDownloads). Pre-caching
    // the next song takes this single slot; a song someone actually asked for never waits for it.
    private readonly SharedDownloads<string?> _downloads = new();
    private readonly SemaphoreSlim _backgroundSlot = new(1, 1);

    // Kept as a running total rather than re-listing the cache folder on every download and
    // eviction step.
    private long _cacheBytes;
    private readonly object _sizeLock = new();

    // Access times are written out every so often, not on every cache hit.
    private static readonly TimeSpan MetadataSaveInterval = TimeSpan.FromSeconds(30);
    private readonly Timer _metadataTimer;
    private int _metadataDirty;

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
        _cacheBytes = MeasureCacheSize();

        _metadataTimer = new Timer(_ => SaveMetadataIfDirty(), null, MetadataSaveInterval, MetadataSaveInterval);
    }

    // A limit at or near zero makes EnsureSpaceAvailable never succeed, silently disabling the
    // cache and forcing every track to stream directly - guard against that regardless of what
    // the server sends.
    private const int MinCacheLimitMb = 50;

    public void UpdateCacheLimit(int cacheLimitMb)
    {
        int clampedMb = Math.Max(MinCacheLimitMb, cacheLimitMb);
        _cacheLimitBytes = (long)clampedMb * 1024 * 1024;
        _logger.LogInformation("SongCache: Cache limit set to {Mb}MB", clampedMb);
    }

    public async Task<string> GetAudioSourceAsync(Guid songId, CancellationToken cancellationToken = default)
    {
        string cachedPath = GetCachePath(songId);

        if (File.Exists(cachedPath))
        {
            _logger.LogInformation("SongCache: Cache hit for {SongId}", songId);
            UpdateAccessTime(songId);
            return cachedPath;
        }

        _logger.LogInformation("SongCache: Cache miss for {SongId}, downloading", songId);

        string? downloaded = await _downloads.GetOrStartAsync(songId, () => DownloadToCache(songId), cancellationToken);

        // Streamed by Media Foundation, which can't send headers: a media token goes in the URL.
        return downloaded ?? await KioskMediaTokens.AppendToAsync(BuildApiUrl(songId));
    }

    public void SetActiveSong(Guid songId)
    {
        _activeSongId = songId;
    }

    public void PreCacheInBackground(Guid songId)
    {
        if (File.Exists(GetCachePath(songId)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // One pre-cache at a time, and never ahead of a song someone asked for: those don't
            // take this slot, and join this download if it's the same song.
            if (!await _backgroundSlot.WaitAsync(0))
            {
                return;
            }

            try
            {
                _logger.LogInformation("SongCache: Pre-caching {SongId} in background", songId);
                await _downloads.GetOrStartAsync(songId, () => DownloadToCache(songId));
            }
            finally
            {
                _backgroundSlot.Release();
            }
        });
    }

    private async Task<string?> DownloadToCache(Guid songId)
    {
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

            using HttpRequestMessage request = new(HttpMethod.Get, url);
            KioskCredentials.ApplyTo(request);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (FileStream fs = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(fs);
            }

            File.Move(tempPath, cachedPath);
            AddToCacheSize(new FileInfo(cachedPath).Length);
            UpdateAccessTime(songId);

            _logger.LogInformation("SongCache: Cached {SongId}", songId);
            return cachedPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SongCache: Failed to download {SongId}", songId);
            return null;
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

        if (CacheSize < _cacheLimitBytes)
        {
            return true;
        }

        EvictLru();
        return CacheSize < _cacheLimitBytes;
    }

    private void EvictLru()
    {
        _logger.LogInformation("SongCache: Cache full, evicting least recently accessed songs");

        List<(Guid id, DateTime lastAccessed, string path, long size)> entries = [];

        foreach (string file in Directory.GetFiles(_cacheDir, "*.mp3"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (Guid.TryParse(name, out Guid id))
            {
                DateTime accessed = _lastAccessed.TryGetValue(id, out DateTime t) ? t : DateTime.MinValue;
                entries.Add((id, accessed, file, new FileInfo(file).Length));
            }
        }

        long targetSize = _cacheLimitBytes * 3 / 4;

        foreach ((Guid id, _, string path, long size) in entries.OrderBy(x => x.lastAccessed))
        {
            if (CacheSize <= targetSize) break;

            // Never evict the song that's currently loaded/playing - its file may still be
            // open for playback, and pulling it out from under the reader would stall playback.
            if (id == _activeSongId) continue;

            try
            {
                File.Delete(path);
                AddToCacheSize(-size);
                _lastAccessed.TryRemove(id, out _);
                _logger.LogInformation("SongCache: Evicted {SongId}", id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SongCache: Failed to evict {SongId}", id);
            }
        }

        MarkMetadataDirty();
    }

    private long CacheSize
    {
        get
        {
            lock (_sizeLock)
            {
                return _cacheBytes;
            }
        }
    }

    private void AddToCacheSize(long bytes)
    {
        lock (_sizeLock)
        {
            _cacheBytes = Math.Max(0, _cacheBytes + bytes);
        }
    }

    private long MeasureCacheSize() =>
        Directory.GetFiles(_cacheDir, "*.mp3").Sum(f => new FileInfo(f).Length);

    private string GetCachePath(Guid songId) =>
        Path.Combine(_cacheDir, $"{songId}.mp3");

    private static string BuildApiUrl(Guid songId)
    {
        string apiUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")! + "/api";

        // No credential here: a download sends it as a header, and a streamed URL gets a media
        // token (KioskMediaTokens).
        return $"{apiUrl}/music/audio/{songId}";
    }

    private void UpdateAccessTime(Guid songId)
    {
        _lastAccessed[songId] = DateTime.UtcNow;
        MarkMetadataDirty();
    }

    private void MarkMetadataDirty() => Interlocked.Exchange(ref _metadataDirty, 1);

    private void SaveMetadataIfDirty()
    {
        if (Interlocked.Exchange(ref _metadataDirty, 0) == 1)
        {
            SaveMetadata();
        }
    }

    private void LoadMetadata()
    {
        if (!File.Exists(_metadataFile)) return;

        try
        {
            string json = File.ReadAllText(_metadataFile);
            _lastAccessed = new ConcurrentDictionary<Guid, DateTime>(JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json) ?? []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SongCache: Failed to load metadata, starting fresh");
            _lastAccessed = new();
        }
    }

    private void SaveMetadata()
    {
        try
        {
            // Written to a temp file and moved, so a crash mid-write can't leave half a file.
            string tempFile = _metadataFile + ".tmp";
            File.WriteAllText(tempFile, JsonSerializer.Serialize(new Dictionary<Guid, DateTime>(_lastAccessed)));
            File.Move(tempFile, _metadataFile, overwrite: true);
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
        _metadataTimer.Dispose();
        SaveMetadataIfDirty();
        _backgroundSlot.Dispose();
        _httpClient.Dispose();
    }
}
