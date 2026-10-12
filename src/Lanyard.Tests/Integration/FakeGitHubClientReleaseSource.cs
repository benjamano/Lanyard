using Lanyard.Application.Services.ClientReleases;

namespace Lanyard.Tests.Integration;

// Stands in for the GitHub Releases API so the test host never reaches the network. Empty unless a
// test adds releases (and the feed files they point at) itself.
public sealed class FakeGitHubClientReleaseSource : IGitHubClientReleaseSource
{
    public List<GitHubClientRelease> Releases { get; } = [];

    public Dictionary<string, string> Files { get; } = [];

    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<GitHubClientRelease>> ListClientReleasesAsync(CancellationToken cancellationToken = default)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<GitHubClientRelease>>(Releases.ToList());
    }

    public Task<string> DownloadStringAsync(string url, CancellationToken cancellationToken = default)
    {
        return Files.TryGetValue(url, out string? content)
            ? Task.FromResult(content)
            : throw new HttpRequestException($"No fake file at {url}");
    }
}
