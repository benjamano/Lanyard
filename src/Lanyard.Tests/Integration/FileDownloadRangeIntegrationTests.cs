using System.Net;
using System.Net.Http.Headers;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Drives the real FilesController / KioskMediaController through the HTTP pipeline (the test host
// runs as Development, so files come from local disk and the unconfigured client secret allows
// anonymous access) to prove Range requests produce a 206 with only the requested bytes.
[TestClass]
public class FileDownloadRangeIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task<FileMetadata> SeedFileAsync(string contentType, int length)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Enumerable.Range(0, length).Select(i => (byte)i).ToArray());

        FileMetadata file = new()
        {
            Id = Guid.NewGuid(),
            FileName = "clip.bin",
            FilePath = path,
            FileSize = length,
            ContentType = contentType,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "test",
            IsActive = true
        };

        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<ApplicationDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using ApplicationDbContext ctx = await dbFactory.CreateDbContextAsync();
        ctx.FileMetadata.Add(file);
        await ctx.SaveChangesAsync();

        return file;
    }

    [TestMethod]
    public async Task FilesDownload_WithRangeHeader_ReturnsOnlyTheRequestedBytes()
    {
        FileMetadata file = await SeedFileAsync("application/octet-stream", 100);
        HttpClient client = _factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/files/download/{file.Id}");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(10, body.Length);
        Assert.AreEqual(10, body[0]);
        Assert.AreEqual(19, body[^1]);
        Assert.IsNotNull(response.Content.Headers.ContentRange);
        Assert.AreEqual(100, response.Content.Headers.ContentRange!.Length);
        Assert.IsTrue(response.Headers.AcceptRanges.Contains("bytes"));
        Assert.IsNotNull(response.Headers.CacheControl);
        Assert.IsTrue(response.Headers.CacheControl!.Private);
    }

    [TestMethod]
    public async Task FilesDownload_WithoutRange_ReturnsWholeFile()
    {
        FileMetadata file = await SeedFileAsync("application/octet-stream", 40);
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/files/download/{file.Id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(40, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    public async Task KioskStepVideo_ResolvesTheStepsVideoAndStreamsIt()
    {
        FileMetadata video = await SeedFileAsync("video/mp4", 64);
        FileMetadata notVideo = await SeedFileAsync("image/png", 8);

        Guid programId = Guid.NewGuid();
        Guid videoStepId = Guid.NewGuid();
        Guid imageStepId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IDbContextFactory<ApplicationDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using ApplicationDbContext ctx = await dbFactory.CreateDbContextAsync();

            ProjectionProgramStepTemplate template = new() { Id = Guid.NewGuid(), Name = "Play a Video", IsActive = true };
            ProjectionProgramStepTemplateParameter videoParameter = new() { Id = Guid.NewGuid(), TemplateId = template.Id, Name = Lanyard.API.Controllers.KioskMediaController.VideoParameterName, DataType = "File", IsActive = true };
            ctx.ProjectionProgramStepTemplates.Add(template);
            ctx.ProjectionProgramStepTemplateParameters.Add(videoParameter);
            ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = programId, Name = "Test program", IsActive = true });
            ctx.ProjectionProgramSteps.Add(new ProjectionProgramStep
            {
                Id = videoStepId, ProjectionProgramId = programId, TemplateId = template.Id, SortOrder = 0, IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = videoParameter.Id, Value = video.Id.ToString() }]
            });
            ctx.ProjectionProgramSteps.Add(new ProjectionProgramStep
            {
                Id = imageStepId, ProjectionProgramId = programId, TemplateId = template.Id, SortOrder = 1, IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = videoParameter.Id, Value = notVideo.Id.ToString() }]
            });
            await ctx.SaveChangesAsync();
        }

        HttpClient client = _factory.CreateClient();

        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, $"/api/kiosk/programs/{programId}/steps/{videoStepId}/video");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 15);
        HttpResponseMessage partial = await client.SendAsync(rangeRequest);

        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.AreEqual("video/mp4", partial.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(16, (await partial.Content.ReadAsByteArrayAsync()).Length);

        // A step whose "video" is not actually a video is refused, as is a step from another program.
        HttpResponseMessage wrongType = await client.GetAsync($"/api/kiosk/programs/{programId}/steps/{imageStepId}/video");
        Assert.AreEqual(HttpStatusCode.NotFound, wrongType.StatusCode);

        HttpResponseMessage wrongProgram = await client.GetAsync($"/api/kiosk/programs/{Guid.NewGuid()}/steps/{videoStepId}/video");
        Assert.AreEqual(HttpStatusCode.NotFound, wrongProgram.StatusCode);
    }
}
