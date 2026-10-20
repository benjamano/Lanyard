using Lanyard.Client.Security;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Updates;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Lanyard.Client.AutoUpdate;

// Asks the Lanyard server which version this kiosk should run instead of taking the newest GitHub
// release. The server answers with a Velopack feed holding exactly that one release (or nothing),
// so pins, canaries and withdrawn releases are all decided server-side. The package itself still
// downloads straight from GitHub, using the URL the server sends alongside the feed.
internal sealed class LanyardServerUpdateSource(Uri serverBaseUrl, Guid clientId, bool allowInsecureSsl) : IUpdateSource
{
    // Velopack's downloader takes its timeout in minutes. A full package is ~100 MB, so this
    // allows for a slow venue connection.
    private const double PackageDownloadTimeoutMinutes = 30;

    private static readonly TimeSpan FeedRequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, string> _downloadUrls = new(StringComparer.OrdinalIgnoreCase);

    public string? TargetVersion { get; private set; }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        using HttpClientHandler handler = new();

        if (allowInsecureSsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using HttpClient httpClient = new(handler) { BaseAddress = serverBaseUrl, Timeout = FeedRequestTimeout };

        using HttpRequestMessage request = new(HttpMethod.Get, ClientUpdateRoutes.Feed(clientId));

        KioskCredentials.ApplyTo(request);

        using HttpResponseMessage response = await httpClient.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        ServerResult? envelope = await response.Content.ReadFromJsonAsync<ServerResult>(JsonOptions).ConfigureAwait(false);

        if (envelope?.IsSuccess != true || envelope.Data is null)
        {
            throw new InvalidOperationException($"The server couldn't say which version to run: {envelope?.Error ?? "empty response"}");
        }

        TargetVersion = envelope.Data.TargetVersion;

        _downloadUrls.Clear();

        foreach (KeyValuePair<string, string> downloadUrl in envelope.Data.DownloadUrls)
        {
            _downloadUrls[downloadUrl.Key] = downloadUrl.Value;
        }

        VelopackAssetFeed feed = VelopackAssetFeed.FromJson(envelope.Data.FeedJson);

        // Nothing is installed unless Lanyard's release workflow signed it (Security Issue 9.9):
        // the hashes Velopack checks the download against come from this same server response, so
        // without the signature a tampered server or connection could hand over any package.
        foreach (VelopackAsset asset in feed.Assets)
        {
            string manifest = ReleaseSigning.Manifest(asset.Version.ToString(), asset.FileName, asset.Size, asset.SHA1, asset.SHA256);

            if (!ReleaseSigning.Verify(manifest, envelope.Data.PackageSignature))
            {
                throw new InvalidOperationException($"Update {asset.Version} isn't signed by Lanyard's release workflow; not installing it.");
            }
        }

        return feed;
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
    {
        if (!_downloadUrls.TryGetValue(releaseEntry.FileName, out string? url))
        {
            throw new InvalidOperationException($"The server didn't say where to download {releaseEntry.FileName} from.");
        }

        if (!ReleaseSigning.IsAllowedDownloadUrl(url))
        {
            throw new InvalidOperationException($"{releaseEntry.FileName} would be downloaded from somewhere other than GitHub over https; not downloading it.");
        }

        Console.WriteLine($"Downloading {releaseEntry.FileName}");

        HttpClientFileDownloader downloader = new();
        await downloader.DownloadFile(url, localFile, progress, headers: null, timeout: PackageDownloadTimeoutMinutes, cancelToken).ConfigureAwait(false);
    }

    // The server wraps every API response in its Result<T> shape.
    private sealed class ServerResult
    {
        public bool IsSuccess { get; set; }
        public ClientUpdateFeedResponse? Data { get; set; }
        public string? Error { get; set; }
    }
}
