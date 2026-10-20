using Lanyard.Client.Security;
using Lanyard.Shared.AudioClips;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;

public interface IAudioClipCacheService
{
    /// <summary>
    /// A local file path for the clip, downloading it first if needed. Falls back to the stream
    /// URL when the download fails or times out, and returns null if the clip's format isn't one
    /// the kiosk plays.
    /// </summary>
    Task<string?> GetAudioSourceAsync(AudioClipPlayCommand command, CancellationToken ct);

    /// <summary>Downloads any of these clips not already on disk, one at a time.</summary>
    Task PrefetchAsync(IEnumerable<AudioClipPlayCommand> commands, CancellationToken ct);
}

/// <summary>
/// Keeps downloaded clips in their own folder under the song cache, so they never count towards
/// the song cache's limit, never get evicted with songs and never show up as cached songs.
/// </summary>
public sealed class AudioClipCacheService : IAudioClipCacheService, IDisposable
{
    // Clips are short; a closing-time call that turns up after closing is no use, so give up and
    // stream rather than wait long for a slow download.
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    private const long CacheLimitBytes = 100L * 1024 * 1024;

    private readonly ILogger<AudioClipCacheService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public AudioClipCacheService(ILogger<AudioClipCacheService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();

        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "LanyardCache", "Clips");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string?> GetAudioSourceAsync(AudioClipPlayCommand command, CancellationToken ct)
    {
        string? fileName = AudioClipFormats.CacheFileName(command.ClipId, command.ContentVersion, command.FileExtension);

        if (fileName is null)
        {
            _logger.LogWarning("ClipCache: Clip {ClipId} has unsupported format {Extension}", command.ClipId, command.FileExtension);
            return null;
        }

        string cachedPath = Path.Combine(_cacheDir, fileName);

        if (File.Exists(cachedPath))
        {
            _logger.LogInformation("ClipCache: Cache hit for clip {ClipId}", command.ClipId);
            TouchQuietly(cachedPath);
            return cachedPath;
        }

        string? downloaded = await DownloadAsync(command.ClipId, cachedPath, ct);

        // Streamed by Media Foundation, which can't send headers: a media token goes in the URL.
        return downloaded ?? await KioskMediaTokens.AppendToAsync(BuildApiUrl(command.ClipId));
    }

    public async Task PrefetchAsync(IEnumerable<AudioClipPlayCommand> commands, CancellationToken ct)
    {
        foreach (AudioClipPlayCommand command in commands)
        {
            string? fileName = AudioClipFormats.CacheFileName(command.ClipId, command.ContentVersion, command.FileExtension);

            if (fileName is null || File.Exists(Path.Combine(_cacheDir, fileName)))
            {
                continue;
            }

            _logger.LogInformation("ClipCache: Downloading scheduled clip {ClipId} ({Name}) ahead", command.ClipId, command.Name);
            await DownloadAsync(command.ClipId, Path.Combine(_cacheDir, fileName), ct);
        }
    }

    private async Task<string?> DownloadAsync(Guid clipId, string cachedPath, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        string tempPath = cachedPath + ".tmp";

        try
        {
            await _downloadLock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ClipCache: Timed out waiting to download clip {ClipId}, streaming instead", clipId);
            return null;
        }

        try
        {
            if (File.Exists(cachedPath))
            {
                return cachedPath;
            }

            _logger.LogInformation("ClipCache: Downloading clip {ClipId}", clipId);

            using HttpRequestMessage request = new(HttpMethod.Get, BuildApiUrl(clipId));
            KioskCredentials.ApplyTo(request);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using (FileStream fs = File.Create(tempPath))
            {
                await response.Content.CopyToAsync(fs, timeout.Token);
            }

            File.Move(tempPath, cachedPath, overwrite: true);

            DeleteOtherVersions(clipId, cachedPath);
            EvictIfOverLimit(cachedPath);

            _logger.LogInformation("ClipCache: Cached clip {ClipId}", clipId);
            return cachedPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipCache: Failed to download clip {ClipId}, streaming instead", clipId);
            DeleteQuietly(tempPath);
            return null;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    // A replaced clip gets a new content version; the old file is never wanted again.
    private void DeleteOtherVersions(Guid clipId, string keepPath)
    {
        foreach (string file in Directory.GetFiles(_cacheDir))
        {
            if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && AudioClipFormats.ClipIdFromCacheFileName(Path.GetFileName(file)) == clipId)
            {
                DeleteQuietly(file);
            }
        }
    }

    private void EvictIfOverLimit(string keepPath)
    {
        List<FileInfo> files = new DirectoryInfo(_cacheDir).GetFiles()
            .Where(x => !x.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.LastAccessTimeUtc)
            .ToList();

        long total = files.Sum(x => x.Length);

        foreach (FileInfo file in files)
        {
            if (total <= CacheLimitBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total -= file.Length;
            DeleteQuietly(file.FullName);
            _logger.LogInformation("ClipCache: Evicted {File}", file.Name);
        }
    }

    private static string BuildApiUrl(Guid clipId)
    {
        string apiUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")! + "/api";

        // No credential here, as for songs: a header when downloading, a media token when streaming.
        return $"{apiUrl}/audioclips/{clipId}/audio";
    }

    private void TouchQuietly(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClipCache: Couldn't update the access time of {Path}", path);
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipCache: Failed to delete {Path}", path);
        }
    }

    public void Dispose()
    {
        _downloadLock.Dispose();
        _httpClient.Dispose();
    }
}
