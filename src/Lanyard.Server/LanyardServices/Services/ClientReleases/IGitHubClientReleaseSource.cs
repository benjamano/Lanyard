namespace Lanyard.Application.Services.ClientReleases;

public sealed record GitHubClientRelease(
    string Tag,
    string Version,
    bool IsPrerelease,
    DateTime PublishedAt,
    IReadOnlyDictionary<string, string> AssetDownloadUrls);

// The GitHub Releases API, narrowed to what the kiosk release catalogue needs. Behind an
// interface so the catalogue is tested without the network.
public interface IGitHubClientReleaseSource
{
    // Published LanyardClient-x.y.z releases, newest first (drafts and other tags left out).
    Task<IReadOnlyList<GitHubClientRelease>> ListClientReleasesAsync(CancellationToken cancellationToken = default);

    Task<string> DownloadStringAsync(string url, CancellationToken cancellationToken = default);
}
