using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lanyard.Client.ProjectionPrograms.Offline;

/// <summary>
/// Keeps %LOCALAPPDATA%\LanyardCache\Projection in step with the server's offline manifest for
/// this kiosk. A new manifest only replaces the old one once every video it needs (and fits) has
/// downloaded, so a sync cut short by the server going down never leaves a half-synced cache.
/// </summary>
public class KioskOfflineCache(ILogger<KioskOfflineCache> logger) : IKioskOfflineCache
{
    private const string ManifestFileName = "manifest.json";
    private const long MinFreeDiskBytes = 200L * 1024 * 1024;

    // A large video over a slow venue connection can take a while.
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<KioskOfflineCache> _logger = logger;
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanyardCache", "Projection");
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private int _syncRequested;

    public string MediaFolder => _root;
    public KioskOfflineManifestDTO? Manifest { get; private set; }
    public IReadOnlySet<Guid> CachedVideoIds { get; private set; } = new HashSet<Guid>();
    public int VideosThatDoNotFit { get; private set; }
    public long CachedBytes { get; private set; }

    public event Action<KioskOfflineManifestDTO>? ManifestChanged;

    public void LoadFromDisk()
    {
        try
        {
            Directory.CreateDirectory(_root);

            // Left behind by a download that was interrupted (power cut, process killed).
            foreach (string tmp in Directory.GetFiles(_root, "*.tmp"))
            {
                File.Delete(tmp);
            }

            string manifestPath = Path.Combine(_root, ManifestFileName);

            if (!File.Exists(manifestPath))
            {
                _logger.LogInformation("Offline cache: nothing cached yet");
                return;
            }

            KioskOfflineManifestDTO? manifest = JsonSerializer.Deserialize<KioskOfflineManifestDTO>(File.ReadAllText(manifestPath), JsonOptions);

            if (manifest == null)
            {
                return;
            }

            ApplyManifest(manifest);

            _logger.LogInformation("Offline cache: loaded manifest {ManifestVersion} with {VideoCount} cached videos", manifest.ManifestVersion, CachedVideoIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache: failed to load the cached manifest, starting empty");
        }
    }

    public void RequestSync()
    {
        Interlocked.Exchange(ref _syncRequested, 1);

        _ = Task.Run(RunSyncsAsync);
    }

    private async Task RunSyncsAsync()
    {
        // Only one sync at a time; a request that arrives mid-sync sets the flag again, so the
        // loop runs once more and picks up the newer state.
        if (!await _syncLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (Interlocked.Exchange(ref _syncRequested, 0) == 1)
            {
                await SyncOnceAsync();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncOnceAsync()
    {
        try
        {
            using HttpClient http = CreateHttpClient();

            KioskOfflineManifestDTO? manifest = await FetchManifestAsync(http);

            if (manifest == null)
            {
                return;
            }

            Directory.CreateDirectory(_root);

            OfflineCachePlanner.CachePlan plan = OfflineCachePlanner.Plan(manifest, ReadCachedFiles(), (long)manifest.CacheLimitMb * 1024 * 1024);

            if (!manifest.OfflineFallbackEnabled)
            {
                // Switched off: keep the settings (so the kiosk knows not to go offline) but free the disk.
                plan = plan with { ToDownload = [], Keep = [], ToDelete = [.. ReadCachedFiles().Keys] };
            }

            foreach (KioskOfflineStepDTO step in plan.ToDownload)
            {
                if (!await DownloadAsync(http, step.VideoFileId!.Value, step.VideoSizeBytes ?? 0))
                {
                    // Server went away, or the file is broken: keep the old manifest and try again
                    // on the next sync. What did download stays for next time.
                    return;
                }
            }

            WriteManifest(manifest);

            foreach (Guid stale in plan.ToDelete)
            {
                TryDelete(Path.Combine(_root, IKioskOfflineCache.VideoFileName(stale)));
            }

            bool changed = Manifest?.ManifestVersion != manifest.ManifestVersion;

            ApplyManifest(manifest);

            if (plan.DoesNotFit.Count > 0)
            {
                _logger.LogWarning("Offline cache: {Count} videos don't fit in the {CacheLimitMb} MB limit and won't play offline", plan.DoesNotFit.Count, manifest.CacheLimitMb);
            }

            _logger.LogInformation("Offline cache: synced manifest {ManifestVersion} ({VideoCount} videos, {CachedMb} MB)", manifest.ManifestVersion, CachedVideoIds.Count, CachedBytes / 1024 / 1024);

            if (changed)
            {
                ManifestChanged?.Invoke(manifest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache: sync failed, keeping the previous cache");
        }
    }

    private async Task<KioskOfflineManifestDTO?> FetchManifestAsync(HttpClient http)
    {
        Guid clientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!);

        using HttpRequestMessage request = CreateRequest(KioskOfflineRoutes.Manifest(clientId));
        using CancellationTokenSource timeout = new(ManifestTimeout);
        using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A server older than this kiosk has no offline endpoints yet.
            _logger.LogInformation("Offline cache: the server doesn't offer an offline manifest yet");
            return null;
        }

        response.EnsureSuccessStatusCode();

        ManifestEnvelope? envelope = await response.Content.ReadFromJsonAsync<ManifestEnvelope>(JsonOptions, timeout.Token);

        if (envelope?.IsSuccess != true || envelope.Data == null)
        {
            _logger.LogWarning("Offline cache: the server couldn't build the manifest: {Error}", envelope?.Error ?? "empty response");
            return null;
        }

        return envelope.Data;
    }

    private async Task<bool> DownloadAsync(HttpClient http, Guid fileId, long expectedBytes)
    {
        string target = Path.Combine(_root, IKioskOfflineCache.VideoFileName(fileId));
        string tmp = target + ".tmp";

        try
        {
            if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < expectedBytes + MinFreeDiskBytes)
            {
                _logger.LogWarning("Offline cache: not enough free disk space for video {FileId}", fileId);
                return false;
            }

            Guid clientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!);

            using HttpRequestMessage request = CreateRequest(KioskOfflineRoutes.Media(clientId, fileId));
            using CancellationTokenSource timeout = new(DownloadTimeout);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using (FileStream file = File.Create(tmp))
            {
                await response.Content.CopyToAsync(file, timeout.Token);
            }

            long downloaded = new FileInfo(tmp).Length;

            if (expectedBytes > 0 && downloaded != expectedBytes)
            {
                _logger.LogWarning("Offline cache: video {FileId} downloaded {Downloaded} bytes, expected {Expected}", fileId, downloaded, expectedBytes);
                TryDelete(tmp);
                return false;
            }

            File.Move(tmp, target, overwrite: true);

            _logger.LogInformation("Offline cache: downloaded video {FileId} ({SizeMb} MB)", fileId, downloaded / 1024 / 1024);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache: failed to download video {FileId}", fileId);
            TryDelete(tmp);
            return false;
        }
    }

    // Written to a temp file and moved over, so a crash mid-write can't leave a corrupt manifest.
    private void WriteManifest(KioskOfflineManifestDTO manifest)
    {
        string path = Path.Combine(_root, ManifestFileName);
        string tmp = path + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private void ApplyManifest(KioskOfflineManifestDTO manifest)
    {
        Dictionary<Guid, long> onDisk = ReadCachedFiles();

        HashSet<Guid> cached = [.. manifest.Displays
            .SelectMany(x => x.Steps)
            .Where(x => x.VideoFileId is Guid id && onDisk.TryGetValue(id, out long size) && size == (x.VideoSizeBytes ?? size))
            .Select(x => x.VideoFileId!.Value)];

        int total = manifest.Displays.SelectMany(x => x.Steps).Where(x => x.VideoFileId != null).Select(x => x.VideoFileId).Distinct().Count();

        Manifest = manifest;
        CachedVideoIds = cached;
        CachedBytes = cached.Sum(id => onDisk[id]);
        VideosThatDoNotFit = total - cached.Count;
    }

    private Dictionary<Guid, long> ReadCachedFiles()
    {
        Dictionary<Guid, long> files = [];

        if (!Directory.Exists(_root))
        {
            return files;
        }

        foreach (string path in Directory.GetFiles(_root, "*.mp4"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                files[id] = new FileInfo(path).Length;
            }
        }

        return files;
    }

    private void TryDelete(string path)
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
            // Usually the offline player still has it open; the next sync tries again.
            _logger.LogWarning(ex, "Offline cache: couldn't delete {Path}", path);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new();

        if (bool.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL"), out bool insecure) && insecure)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        // Per-request timeouts are applied with cancellation tokens instead.
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")!.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static HttpRequestMessage CreateRequest(string path)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);

        string? secret = Environment.GetEnvironmentVariable("LANYARD_CLIENT_SHARED_SECRET");

        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.Add(ClientConnectionQuery.SecretHeader, secret);
        }

        return request;
    }

    // The server wraps the manifest in its Result<T> envelope.
    private sealed class ManifestEnvelope
    {
        public bool IsSuccess { get; set; }
        public string? Error { get; set; }
        public KioskOfflineManifestDTO? Data { get; set; }
    }
}
