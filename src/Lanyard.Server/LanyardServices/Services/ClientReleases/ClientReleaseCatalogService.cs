using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Lanyard.Shared.Updates;

namespace Lanyard.Application.Services.ClientReleases;

public sealed class ClientReleaseCatalogService(
    IDbContextFactory<ApplicationDbContext> factory,
    IGitHubClientReleaseSource gitHub,
    IMemoryCache cache,
    ILogger<ClientReleaseCatalogService> logger) : IClientReleaseCatalogService
{
    // Velopack writes one feed per channel; the pipeline packs with the default Windows channel.
    public const string FeedAssetName = "releases.win.json";

    private const string SyncedCacheKey = "ClientReleases:SyncedFromGitHub";
    private const string GitHubListCacheKey = "ClientReleases:GitHubList";
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    // Every kiosk checks for updates when it starts, so a power cut can bring a whole venue in at
    // once. One sync at a time keeps that to a single GitHub call and no duplicate inserts.
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IGitHubClientReleaseSource _gitHub = gitHub;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<ClientReleaseCatalogService> _logger = logger;

    public async Task<Result<IReadOnlyList<ClientRelease>>> GetReleasesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncFromGitHubAsync(forceRefresh, cancellationToken);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<ClientRelease> releases = await ctx.ClientReleases
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(cancellationToken);

            releases.Sort((left, right) => ClientVersionFormat.Compare(right.Version, left.Version));

            return Result<IReadOnlyList<ClientRelease>>.Ok(releases);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load kiosk releases");
            return Result<IReadOnlyList<ClientRelease>>.Fail($"Failed to load kiosk releases: {ex.Message}");
        }
    }

    public async Task<Result<ClientUpdateFeedResponse>> GetFeedForVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        try
        {
            // A published release's files never change, so its feed only needs building once.
            string cacheKey = $"ClientReleases:Feed:{version}";

            if (_cache.TryGetValue(cacheKey, out ClientUpdateFeedResponse? cached) && cached is not null)
            {
                return Result<ClientUpdateFeedResponse>.Ok(cached);
            }

            if (!_cache.TryGetValue(GitHubListCacheKey, out IReadOnlyList<GitHubClientRelease>? gitHubReleases) || gitHubReleases is null)
            {
                gitHubReleases = await _gitHub.ListClientReleasesAsync(cancellationToken);
                _cache.Set(GitHubListCacheKey, gitHubReleases, SyncInterval);
            }

            GitHubClientRelease? release = gitHubReleases.FirstOrDefault(x => x.Version == version);

            if (release is null)
            {
                return Result<ClientUpdateFeedResponse>.Fail($"Kiosk release v{version} isn't on GitHub.");
            }

            if (!release.AssetDownloadUrls.TryGetValue(FeedAssetName, out string? feedUrl))
            {
                return Result<ClientUpdateFeedResponse>.Fail($"Kiosk release v{version} has no {FeedAssetName}.");
            }

            string feedJson = await _gitHub.DownloadStringAsync(feedUrl, cancellationToken);

            Result<ClientUpdateFeedResponse> built = BuildSingleVersionFeed(feedJson, version, release.AssetDownloadUrls);

            if (built.IsSuccess && built.Data is not null)
            {
                // Signed by the release workflow (see ReleaseSigning). Passed through as-is: the
                // kiosk checks it, so a server that has been tampered with can't vouch for a package.
                string packageFileName = built.Data.DownloadUrls.Keys.Single();

                if (release.AssetDownloadUrls.TryGetValue(ReleaseSigning.SignatureFileName(packageFileName), out string? signatureUrl))
                {
                    built.Data.PackageSignature = (await _gitHub.DownloadStringAsync(signatureUrl, cancellationToken)).Trim();
                }
                else
                {
                    _logger.LogWarning("Kiosk release {Version} has no signature file; kiosks on 1.0.44 or later won't install it", version);
                }

                _cache.Set(cacheKey, built.Data, TimeSpan.FromHours(1));
            }
            else
            {
                _logger.LogWarning("Could not build the update feed for kiosk release {Version}: {Error}", version, built.Error);
            }

            return built;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the update feed for kiosk release {Version}", version);
            return Result<ClientUpdateFeedResponse>.Fail($"Failed to build the update feed for v{version}: {ex.Message}");
        }
    }

    // Keeps only the target's full package, untouched, so Velopack checks it against the same
    // SHA1/SHA256/Size it was published with. Delta packages are dropped: a delta only applies on
    // top of one exact earlier version, and a pinned or downgrading kiosk may not be on it.
    public static Result<ClientUpdateFeedResponse> BuildSingleVersionFeed(string feedJson, string version, IReadOnlyDictionary<string, string> assetDownloadUrls)
    {
        JsonNode? root = JsonNode.Parse(feedJson);
        JsonArray? assets = root?["Assets"] as JsonArray;

        if (assets is null)
        {
            return Result<ClientUpdateFeedResponse>.Fail("The release feed has no Assets list.");
        }

        JsonNode? fullAsset = assets.FirstOrDefault(x =>
            string.Equals(x?["Type"]?.ToString(), "Full", StringComparison.OrdinalIgnoreCase)
            && x?["Version"]?.ToString() == version);

        string? fileName = fullAsset?["FileName"]?.ToString();

        if (fullAsset is null || string.IsNullOrEmpty(fileName))
        {
            return Result<ClientUpdateFeedResponse>.Fail($"The release feed has no full package for v{version}.");
        }

        if (!assetDownloadUrls.TryGetValue(fileName, out string? downloadUrl))
        {
            return Result<ClientUpdateFeedResponse>.Fail($"The release has no file called {fileName}.");
        }

        JsonObject singleFeed = new()
        {
            ["Assets"] = new JsonArray(fullAsset.DeepClone())
        };

        return Result<ClientUpdateFeedResponse>.Ok(new ClientUpdateFeedResponse
        {
            TargetVersion = version,
            FeedJson = singleFeed.ToJsonString(),
            DownloadUrls = new Dictionary<string, string> { [fileName] = downloadUrl }
        });
    }

    private async Task SyncFromGitHubAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _cache.TryGetValue(SyncedCacheKey, out _))
        {
            return;
        }

        await SyncLock.WaitAsync(cancellationToken);

        try
        {
            // Another caller may have synced while this one waited for the lock.
            if (!force && _cache.TryGetValue(SyncedCacheKey, out _))
            {
                return;
            }

            IReadOnlyList<GitHubClientRelease> gitHubReleases;

            try
            {
                gitHubReleases = await _gitHub.ListClientReleasesAsync(cancellationToken);
                _cache.Set(GitHubListCacheKey, gitHubReleases, SyncInterval);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kiosks keep resolving against what's already stored; retry on a shorter fuse.
                _logger.LogWarning(ex, "Could not refresh kiosk releases from GitHub; using the stored list");
                _cache.Set(SyncedCacheKey, true, TimeSpan.FromMinutes(1));
                return;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Dictionary<string, ClientRelease> existing = await ctx.ClientReleases
                .TagWithCallSite()
                .ToDictionaryAsync(x => x.Version, cancellationToken);

            int added = 0;

            foreach (GitHubClientRelease gitHubRelease in gitHubReleases)
            {
                if (existing.TryGetValue(gitHubRelease.Version, out ClientRelease? stored))
                {
                    stored.Tag = gitHubRelease.Tag;
                    stored.IsPrerelease = gitHubRelease.IsPrerelease;
                    stored.PublishedAt = gitHubRelease.PublishedAt;
                    continue;
                }

                ctx.ClientReleases.Add(new ClientRelease
                {
                    Version = gitHubRelease.Version,
                    Tag = gitHubRelease.Tag,
                    IsPrerelease = gitHubRelease.IsPrerelease,
                    PublishedAt = gitHubRelease.PublishedAt,
                    CreateDate = DateTime.UtcNow
                });

                added++;
            }

            await ctx.SaveChangesAsync(cancellationToken);

            if (added > 0)
            {
                _logger.LogInformation("Found {Count} new kiosk release(s) on GitHub", added);
            }

            _cache.Set(SyncedCacheKey, true, SyncInterval);
        }
        finally
        {
            SyncLock.Release();
        }
    }
}
