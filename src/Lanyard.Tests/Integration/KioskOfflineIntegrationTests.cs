using Lanyard.Application.Services.Authentication;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Projection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The offline manifest and media endpoints through the real pipeline, behind the kiosk's own credential.
[TestClass]
public class KioskOfflineIntegrationTests
{
    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(_ => { });
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<(Guid ClientId, Guid VideoFileId, Guid OtherFileId)> SeedAsync()
    {
        Guid clientId = Guid.NewGuid();
        Guid programId = Guid.NewGuid();
        Guid templateId = Guid.NewGuid();
        Guid videoParameterId = Guid.NewGuid();

        string videoPath = Path.GetTempFileName();
        File.WriteAllBytes(videoPath, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        FileMetadata video = new() { Id = Guid.NewGuid(), FileName = "intro.mp4", FilePath = videoPath, FileSize = 64, ContentType = "video/mp4", IsActive = true };
        FileMetadata other = new() { Id = Guid.NewGuid(), FileName = "secret.mp4", FilePath = videoPath, FileSize = 64, ContentType = "video/mp4", IsActive = true };

        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<ApplicationDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using ApplicationDbContext ctx = await dbFactory.CreateDbContextAsync();

        ctx.FileMetadata.AddRange(video, other);
        ctx.Clients.Add(new Client { Id = clientId, Name = "Arena kiosk" });
        ctx.ProjectionProgramStepTemplates.Add(new ProjectionProgramStepTemplate { Id = templateId, Name = ProjectionTemplateNames.PlayVideo, IsActive = true });
        ctx.ProjectionProgramStepTemplateParameters.Add(new ProjectionProgramStepTemplateParameter { Id = videoParameterId, TemplateId = templateId, Name = ProjectionParameterNames.Video, DataType = "File", IsActive = true });
        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = programId, Name = "Arena loop", IsActive = true });
        ctx.ProjectionProgramSteps.Add(new ProjectionProgramStep
        {
            Id = Guid.NewGuid(), ProjectionProgramId = programId, TemplateId = templateId, SortOrder = 0, IsActive = true,
            ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = videoParameterId, Value = video.Id.ToString() }],
        });
        ctx.ClientProjectionSettings.Add(new ClientProjectionSettings { Id = Guid.NewGuid(), ClientId = clientId, ProjectionProgramId = programId, DisplayIndex = 0, Width = 1920, Height = 1080, IsActive = true });
        await ctx.SaveChangesAsync();

        return (clientId, video.Id, other.Id);
    }

    // The seeded kiosk's own credential.
    private async Task<string> TokenForAsync(Guid clientId)
    {
        Result<IssuedKioskCredential> issued = await _factory.Services.GetRequiredService<IKioskCredentialService>()
            .IssueAsync(clientId, Lanyard.Infrastructure.Enum.ClientCredentialSource.Pairing, null);

        return issued.Data!.RawToken;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? token, RangeHeaderValue? range = null)
    {
        HttpClient http = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/" + path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Range = range;

        return await http.SendAsync(request);
    }

    [TestMethod]
    public async Task Manifest_WithoutACredential_IsRejected()
    {
        var (clientId, _, _) = await SeedAsync();

        HttpResponseMessage response = await GetAsync(KioskOfflineRoutes.Manifest(clientId), token: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Manifest_WithItsCredential_ListsTheKiosksVideo()
    {
        var (clientId, videoFileId, _) = await SeedAsync();

        HttpResponseMessage response = await GetAsync(KioskOfflineRoutes.Manifest(clientId), await TokenForAsync(clientId));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Result<KioskOfflineManifestDTO>? body = await response.Content.ReadFromJsonAsync<Result<KioskOfflineManifestDTO>>();
        Assert.IsNotNull(body?.Data);
        Assert.AreEqual(videoFileId, body.Data.Displays.Single().Steps.Single().VideoFileId);
        Assert.IsFalse(string.IsNullOrEmpty(body.Data.ManifestVersion));
    }

    [TestMethod]
    public async Task Media_ServesTheKiosksOwnVideo_WithRangeSupport()
    {
        var (clientId, videoFileId, _) = await SeedAsync();

        HttpResponseMessage response = await GetAsync(KioskOfflineRoutes.Media(clientId, videoFileId), await TokenForAsync(clientId), new RangeHeaderValue(0, 9));

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual(10, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    public async Task Media_RefusesAFileThatIsNotInTheKiosksManifest()
    {
        var (clientId, _, otherFileId) = await SeedAsync();

        HttpResponseMessage response = await GetAsync(KioskOfflineRoutes.Media(clientId, otherFileId), await TokenForAsync(clientId));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Media_WithoutACredential_IsRejected()
    {
        var (clientId, videoFileId, _) = await SeedAsync();

        HttpResponseMessage response = await GetAsync(KioskOfflineRoutes.Media(clientId, videoFileId), token: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
