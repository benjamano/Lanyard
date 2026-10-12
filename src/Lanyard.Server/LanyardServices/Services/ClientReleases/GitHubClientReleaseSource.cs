using Lanyard.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lanyard.Application.Services.ClientReleases;

public sealed class GitHubClientReleaseSource : IGitHubClientReleaseSource
{
    public const string TagPrefix = "LanyardClient-";
    public const string DefaultRepository = "benjamano/Lanyard";

    private readonly HttpClient _httpClient;
    private readonly string _repository;

    public GitHubClientReleaseSource(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _repository = configuration["ClientReleases:GitHubRepository"] is { Length: > 0 } repository
            ? repository
            : DefaultRepository;

        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");

        // GitHub rejects API calls without a User-Agent.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lanyard", "1.0"));
        }

        // Optional: without a token GitHub allows 60 requests an hour per IP, which the catalogue's
        // cache keeps well under, but a token removes the ceiling.
        if (configuration["ClientReleases:GitHubToken"] is { Length: > 0 } token && _httpClient.DefaultRequestHeaders.Authorization is null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<GitHubClientRelease>> ListClientReleasesAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"repos/{_repository}/releases?per_page=100");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        List<GitHubReleaseJson> releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseJson>>(body, cancellationToken: cancellationToken) ?? [];

        List<GitHubClientRelease> clientReleases = [];

        foreach (GitHubReleaseJson release in releases)
        {
            if (release.Draft || release.TagName is null || !release.TagName.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ClientVersionFormat.TryNormalize(release.TagName[TagPrefix.Length..], out string version))
            {
                continue;
            }

            Dictionary<string, string> assets = (release.Assets ?? [])
                .Where(x => x.Name is not null && x.BrowserDownloadUrl is not null)
                .GroupBy(x => x.Name!)
                .ToDictionary(x => x.Key, x => x.First().BrowserDownloadUrl!);

            clientReleases.Add(new GitHubClientRelease(
                release.TagName,
                version,
                release.Prerelease,
                release.PublishedAt ?? DateTime.UtcNow,
                assets));
        }

        return clientReleases;
    }

    public async Task<string> DownloadStringAsync(string url, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class GitHubReleaseJson
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetJson>? Assets { get; set; }
    }

    private sealed class GitHubAssetJson
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
