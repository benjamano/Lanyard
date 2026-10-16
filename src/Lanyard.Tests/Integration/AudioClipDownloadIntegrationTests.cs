using System.Net;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The kiosk's clip download through the real pipeline, behind the kiosk shared secret.
[TestClass]
public class AudioClipDownloadIntegrationTests
{
    private const string SharedSecret = "integration-test-kiosk-secret";

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<Guid> SeedClipAsync(string fileName, bool deleted = false)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        FileMetadata file = new() { Id = Guid.NewGuid(), FileName = fileName, FilePath = path, FileSize = 32, ContentType = "text/plain", IsActive = true };
        AudioClip clip = new() { Id = Guid.NewGuid(), Name = "Closing time", FileMetadataId = file.Id, DurationSeconds = 1, CreateDate = DateTime.UtcNow, DeleteDate = deleted ? DateTime.UtcNow : null };

        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<ApplicationDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using ApplicationDbContext ctx = await dbFactory.CreateDbContextAsync();

        ctx.FileMetadata.Add(file);
        ctx.AudioClips.Add(clip);
        await ctx.SaveChangesAsync();

        return clip.Id;
    }

    private async Task<HttpResponseMessage> GetAsync(Guid clipId, string? secret)
    {
        HttpClient http = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/audioclips/{clipId}/audio");

        if (secret is not null)
        {
            request.Headers.Add(ClientConnectionQuery.SecretHeader, secret);
        }

        return await http.SendAsync(request);
    }

    [TestMethod]
    public async Task Audio_WithoutTheSecret_IsRejected()
    {
        Guid clipId = await SeedClipAsync("closing.mp3");

        using HttpResponseMessage response = await GetAsync(clipId, null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Audio_WithTheSecret_IsServedWithTheTypeOfItsExtension()
    {
        Guid clipId = await SeedClipAsync("birthday.wav");

        using HttpResponseMessage response = await GetAsync(clipId, SharedSecret);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(32, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    public async Task Audio_ForADeletedClip_IsNotFound()
    {
        Guid clipId = await SeedClipAsync("closing.mp3", deleted: true);

        using HttpResponseMessage response = await GetAsync(clipId, SharedSecret);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
