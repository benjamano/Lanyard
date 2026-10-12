using Lanyard.Application.Services.ClientReleases;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Json.Nodes;

namespace Lanyard.Tests.Services.ClientReleases;

[TestClass]
public class ClientReleaseCatalogServiceTests
{
    // Shape of a real releases.win.json (LanyardClient-1.0.40), trimmed, plus a delta entry.
    private const string FeedJson = """
        {"Assets":[
          {"PackageId":"LanyardClient","Version":"1.0.41","Type":"Full","FileName":"LanyardClient-1.0.41-full.nupkg","SHA1":"A8B3","SHA256":"31DE","Size":106781712,"NotesMarkdown":"notes"},
          {"PackageId":"LanyardClient","Version":"1.0.41","Type":"Delta","FileName":"LanyardClient-1.0.41-delta.nupkg","SHA1":"BBBB","SHA256":"CCCC","Size":1000}
        ]}
        """;

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ClientReleaseCatalogService GetService(DbContextOptions<ApplicationDbContext> options, IGitHubClientReleaseSource gitHub)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new ClientReleaseCatalogService(
            factoryMock.Object,
            gitHub,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ClientReleaseCatalogService>.Instance);
    }

    private static GitHubClientRelease GitHubRelease(string version, bool prerelease = false) => new(
        $"LanyardClient-{version}",
        version,
        prerelease,
        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        new Dictionary<string, string>
        {
            ["releases.win.json"] = $"https://github.example/{version}/releases.win.json",
            [$"LanyardClient-{version}-full.nupkg"] = $"https://github.example/{version}/LanyardClient-{version}-full.nupkg"
        });

    [TestMethod]
    public async Task GetReleasesAsync_MirrorsGitHubReleases_NewestFirst()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        FakeGitHubClientReleaseSource gitHub = new();
        gitHub.Releases.AddRange([GitHubRelease("1.0.9"), GitHubRelease("1.0.10", prerelease: true)]);

        Result<IReadOnlyList<ClientRelease>> result = await GetService(options, gitHub).GetReleasesAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "1.0.10", "1.0.9" }, result.Data!.Select(x => x.Version).ToArray());
        Assert.IsTrue(result.Data!.Single(x => x.Version == "1.0.10").IsPrerelease);
    }

    [TestMethod]
    public async Task GetReleasesAsync_KeepsPromotionAndWithdrawal_WhenResyncing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ClientReleases.Add(new ClientRelease { Version = "1.0.41", Tag = "LanyardClient-1.0.41", IsPrerelease = true, PromotedToStableAt = DateTime.UtcNow, WithdrawnAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        FakeGitHubClientReleaseSource gitHub = new();
        gitHub.Releases.Add(GitHubRelease("1.0.41", prerelease: true));

        Result<IReadOnlyList<ClientRelease>> result = await GetService(options, gitHub).GetReleasesAsync(forceRefresh: true);

        ClientRelease release = result.Data!.Single();
        Assert.IsNotNull(release.PromotedToStableAt);
        Assert.IsNotNull(release.WithdrawnAt);
    }

    [TestMethod]
    public async Task GetReleasesAsync_ReturnsStoredReleases_WhenGitHubIsDown()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ClientReleases.Add(new ClientRelease { Version = "1.0.41", Tag = "LanyardClient-1.0.41" });
            await ctx.SaveChangesAsync();
        }

        Mock<IGitHubClientReleaseSource> gitHub = new();
        gitHub.Setup(x => x.ListClientReleasesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GitHub is down"));

        Result<IReadOnlyList<ClientRelease>> result = await GetService(options, gitHub.Object).GetReleasesAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("1.0.41", result.Data!.Single().Version);
    }

    [TestMethod]
    public async Task GetReleasesAsync_OnlyCallsGitHubOnce_WithinTheRefreshInterval()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        FakeGitHubClientReleaseSource gitHub = new();
        gitHub.Releases.Add(GitHubRelease("1.0.41"));
        ClientReleaseCatalogService service = GetService(options, gitHub);

        await service.GetReleasesAsync();
        await service.GetReleasesAsync();

        Assert.AreEqual(1, gitHub.ListCalls);
    }

    [TestMethod]
    public async Task GetFeedForVersionAsync_ReturnsOnlyTheFullPackage_WithItsDownloadUrl()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        FakeGitHubClientReleaseSource gitHub = new();
        gitHub.Releases.Add(GitHubRelease("1.0.41"));
        gitHub.Files["https://github.example/1.0.41/releases.win.json"] = FeedJson;

        Result<ClientUpdateFeedResponse> result = await GetService(options, gitHub).GetFeedForVersionAsync("1.0.41");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("1.0.41", result.Data!.TargetVersion);

        JsonArray assets = (JsonArray)JsonNode.Parse(result.Data.FeedJson)!["Assets"]!;
        Assert.AreEqual(1, assets.Count, "Delta packages must be dropped.");
        Assert.AreEqual("LanyardClient-1.0.41-full.nupkg", assets[0]!["FileName"]!.ToString(), "FileName must stay a bare file name; Velopack uses it as the local path.");
        Assert.AreEqual("31DE", assets[0]!["SHA256"]!.ToString(), "Checksums must pass through untouched.");
        Assert.AreEqual("https://github.example/1.0.41/LanyardClient-1.0.41-full.nupkg", result.Data.DownloadUrls["LanyardClient-1.0.41-full.nupkg"]);
    }

    [TestMethod]
    public async Task GetFeedForVersionAsync_Fails_ForAVersionGitHubDoesNotHave()
    {
        FakeGitHubClientReleaseSource gitHub = new();

        Result<ClientUpdateFeedResponse> result = await GetService(GetInMemoryOptions(), gitHub).GetFeedForVersionAsync("1.0.99");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void BuildSingleVersionFeed_Fails_WhenFeedHasNoFullPackageForTheVersion()
    {
        Result<ClientUpdateFeedResponse> result = ClientReleaseCatalogService.BuildSingleVersionFeed(
            FeedJson, "1.0.42", new Dictionary<string, string>());

        Assert.IsFalse(result.IsSuccess);
    }
}
