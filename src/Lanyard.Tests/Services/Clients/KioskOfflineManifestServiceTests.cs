using Lanyard.Application.Services;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Projection;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskOfflineManifestServiceTests
{
    private const int CompanyId = 7;

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static KioskOfflineManifestService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new KioskOfflineManifestService(factoryMock.Object, new TestClock(new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc)), NullLogger<KioskOfflineManifestService>.Instance);
    }

    // One kiosk, one screen, one program with every kind of step.
    private sealed class Seed
    {
        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid ProgramId { get; } = Guid.NewGuid();
        public Guid TextStepId { get; } = Guid.NewGuid();
        public Guid VideoStepId { get; } = Guid.NewGuid();
        public Guid VideoFileId { get; } = Guid.NewGuid();
        public Guid TextParameterId { get; } = Guid.NewGuid();
        public Guid VideoParameterId { get; } = Guid.NewGuid();
        public Guid VolumeParameterId { get; } = Guid.NewGuid();
        public Guid RepeatParameterId { get; } = Guid.NewGuid();
        public Guid ShowTextTemplateId { get; } = Guid.NewGuid();
        public Guid PlayVideoTemplateId { get; } = Guid.NewGuid();
        public Guid DelayTemplateId { get; } = Guid.NewGuid();
        public Guid DashboardTemplateId { get; } = Guid.NewGuid();
    }

    private static async Task<Seed> SeedAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Seed seed = new();

        await using ApplicationDbContext ctx = new(options);

        ctx.Clients.Add(new Client { Id = seed.ClientId, CompanyId = CompanyId, Name = "Arena kiosk", OfflineAfterSeconds = 45, ProjectionCacheLimitMb = 1024 });

        ctx.ProjectionProgramStepTemplates.AddRange(
            new ProjectionProgramStepTemplate { Id = seed.ShowTextTemplateId, Name = ProjectionTemplateNames.ShowText, IsActive = true },
            new ProjectionProgramStepTemplate { Id = seed.PlayVideoTemplateId, Name = ProjectionTemplateNames.PlayVideo, IsActive = true },
            new ProjectionProgramStepTemplate { Id = seed.DelayTemplateId, Name = ProjectionTemplateNames.Delay, IsActive = true },
            new ProjectionProgramStepTemplate { Id = seed.DashboardTemplateId, Name = ProjectionTemplateNames.Dashboard, IsActive = true });

        ctx.ProjectionProgramStepTemplateParameters.AddRange(
            new ProjectionProgramStepTemplateParameter { Id = seed.TextParameterId, TemplateId = seed.ShowTextTemplateId, Name = ProjectionParameterNames.Text, DataType = "Text", IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.VideoParameterId, TemplateId = seed.PlayVideoTemplateId, Name = ProjectionParameterNames.Video, DataType = "File", IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.VolumeParameterId, TemplateId = seed.PlayVideoTemplateId, Name = ProjectionParameterNames.Volume, DataType = "Number", IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.RepeatParameterId, TemplateId = seed.PlayVideoTemplateId, Name = ProjectionParameterNames.ShouldRepeat, DataType = "Bool", IsActive = true });

        ctx.FileMetadata.Add(new FileMetadata { Id = seed.VideoFileId, CompanyId = CompanyId, FileName = "intro.mp4", FilePath = "intro.mp4", FileSize = 5_000_000, ContentType = "video/mp4", IsActive = true });

        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = seed.ProgramId, CompanyId = CompanyId, Name = "Arena loop", IsActive = true });

        ctx.ProjectionProgramSteps.AddRange(
            new ProjectionProgramStep
            {
                Id = seed.TextStepId, ProjectionProgramId = seed.ProgramId, TemplateId = seed.ShowTextTemplateId, SortOrder = 0, HoldForMilliseconds = 8000, IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = seed.TextParameterId, Value = "Welcome!" }],
            },
            new ProjectionProgramStep
            {
                Id = seed.VideoStepId, ProjectionProgramId = seed.ProgramId, TemplateId = seed.PlayVideoTemplateId, SortOrder = 1, HoldForMilliseconds = 30000, IsActive = true,
                ParameterValues =
                [
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = seed.VideoParameterId, Value = seed.VideoFileId.ToString() },
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = seed.VolumeParameterId, Value = "40" },
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = seed.RepeatParameterId, Value = "true" },
                ],
            },
            new ProjectionProgramStep { Id = Guid.NewGuid(), ProjectionProgramId = seed.ProgramId, TemplateId = seed.DashboardTemplateId, SortOrder = 2, IsActive = true },
            new ProjectionProgramStep { Id = Guid.NewGuid(), ProjectionProgramId = seed.ProgramId, TemplateId = seed.DelayTemplateId, SortOrder = 3, HoldForMilliseconds = 2000, IsActive = true },
            new ProjectionProgramStep { Id = Guid.NewGuid(), ProjectionProgramId = seed.ProgramId, TemplateId = seed.ShowTextTemplateId, SortOrder = 4, IsActive = false });

        ctx.ClientProjectionSettings.Add(new ClientProjectionSettings
        {
            Id = Guid.NewGuid(), ClientId = seed.ClientId, ProjectionProgramId = seed.ProgramId, DisplayIndex = 1,
            Width = 1920, Height = 1080, IsDarkTheme = true, RepeatInfinitely = true, IsActive = true,
        });

        await ctx.SaveChangesAsync();

        return seed;
    }

    [TestMethod]
    public async Task BuildManifestAsync_KeepsOnlyStepsThatCanPlayOffline_InOrder()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        Result<KioskOfflineManifestDTO> result = await GetService(options).BuildManifestAsync(seed.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        KioskOfflineDisplayDTO display = result.Data!.Displays.Single();

        Assert.AreEqual(1, display.DisplayIndex);
        Assert.AreEqual("Arena loop", display.ProgramName);
        Assert.IsTrue(display.IsDarkTheme);
        CollectionAssert.AreEqual(
            new[] { ProjectionTemplateNames.ShowText, ProjectionTemplateNames.PlayVideo, ProjectionTemplateNames.Delay },
            display.Steps.Select(x => x.TemplateName).ToArray(),
            "The dashboard step is left out and the deleted step never appears.");
        Assert.AreEqual(1, display.SkippedStepCount, "Only the dashboard counts as skipped; deleted steps aren't part of the program.");
    }

    [TestMethod]
    public async Task BuildManifestAsync_CarriesTextVideoAndSettings()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        Result<KioskOfflineManifestDTO> result = await GetService(options).BuildManifestAsync(seed.ClientId);

        KioskOfflineManifestDTO manifest = result.Data!;
        Assert.IsTrue(manifest.OfflineFallbackEnabled);
        Assert.AreEqual(45, manifest.OfflineAfterSeconds);
        Assert.AreEqual(1024, manifest.CacheLimitMb);

        KioskOfflineStepDTO text = manifest.Displays[0].Steps[0];
        Assert.AreEqual("Welcome!", text.Text);
        Assert.AreEqual(8000, text.HoldForMilliseconds);

        KioskOfflineStepDTO video = manifest.Displays[0].Steps[1];
        Assert.AreEqual(seed.VideoFileId, video.VideoFileId);
        Assert.AreEqual(5_000_000, video.VideoSizeBytes);
        Assert.AreEqual(40, video.VideoVolume);
        Assert.IsTrue(video.VideoShouldRepeat);
    }

    [TestMethod]
    public async Task BuildManifestAsync_SkipsAVideoStepWhoseFileIsMissing()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.FileMetadata.Remove(await ctx.FileMetadata.SingleAsync(x => x.Id == seed.VideoFileId));
            await ctx.SaveChangesAsync();
        }

        Result<KioskOfflineManifestDTO> result = await GetService(options).BuildManifestAsync(seed.ClientId);

        KioskOfflineDisplayDTO display = result.Data!.Displays.Single();
        Assert.IsFalse(display.Steps.Any(x => x.TemplateName == ProjectionTemplateNames.PlayVideo));
        Assert.AreEqual(2, display.SkippedStepCount);
    }

    [TestMethod]
    public async Task BuildManifestAsync_IgnoresAnotherCompanysVideoAndProgram()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.FileMetadata.SingleAsync(x => x.Id == seed.VideoFileId)).CompanyId = CompanyId + 1;
            await ctx.SaveChangesAsync();
        }

        Result<KioskOfflineManifestDTO> videoResult = await GetService(options).BuildManifestAsync(seed.ClientId);
        Assert.IsFalse(videoResult.Data!.Displays.Single().Steps.Any(x => x.VideoFileId == seed.VideoFileId));

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.ProjectionPrograms.SingleAsync(x => x.Id == seed.ProgramId)).CompanyId = CompanyId + 1;
            await ctx.SaveChangesAsync();
        }

        Result<KioskOfflineManifestDTO> programResult = await GetService(options).BuildManifestAsync(seed.ClientId);
        Assert.AreEqual(0, programResult.Data!.Displays.Count);
    }

    [TestMethod]
    public async Task BuildManifestAsync_LeavesOutADeletedProgram()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.ProjectionPrograms.SingleAsync(x => x.Id == seed.ProgramId)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<KioskOfflineManifestDTO> result = await GetService(options).BuildManifestAsync(seed.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Displays.Count);
    }

    [TestMethod]
    public async Task BuildManifestAsync_UnknownClient_Fails()
    {
        var options = GetInMemoryOptions();

        Result<KioskOfflineManifestDTO> result = await GetService(options).BuildManifestAsync(Guid.NewGuid());

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ManifestVersion_IsStableUntilSomethingThatPlaysChanges()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);
        KioskOfflineManifestService service = GetService(options);

        string first = (await service.BuildManifestAsync(seed.ClientId)).Data!.ManifestVersion;
        string again = (await service.BuildManifestAsync(seed.ClientId)).Data!.ManifestVersion;
        Assert.AreEqual(first, again);

        await using (ApplicationDbContext ctx = new(options))
        {
            ProjectionProgramParameterValue text = await ctx.Set<ProjectionProgramParameterValue>().SingleAsync(x => x.ParameterId == seed.TextParameterId);
            text.Value = "Welcome back!";
            await ctx.SaveChangesAsync();
        }

        string edited = (await service.BuildManifestAsync(seed.ClientId)).Data!.ManifestVersion;
        Assert.AreNotEqual(first, edited);
    }

    [TestMethod]
    public async Task IsFileInManifestAsync_OnlyAcceptsThisKiosksVideos()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);
        KioskOfflineManifestService service = GetService(options);

        Assert.IsTrue((await service.IsFileInManifestAsync(seed.ClientId, seed.VideoFileId)).Data);
        Assert.IsFalse((await service.IsFileInManifestAsync(seed.ClientId, Guid.NewGuid())).Data);
        Assert.IsFalse((await service.IsFileInManifestAsync(Guid.NewGuid(), seed.VideoFileId)).IsSuccess);
    }

    private static async Task BindTextStepAsync(DbContextOptions<ApplicationDbContext> options, Seed seed, string bindingKey, string? fallback)
    {
        await using ApplicationDbContext ctx = new(options);

        ProjectionProgramParameterValue text = await ctx.Set<ProjectionProgramParameterValue>()
            .SingleAsync(x => x.ProjectionProgramStepId == seed.TextStepId && x.ParameterId == seed.TextParameterId);

        text.BindingKey = bindingKey;
        text.Value = fallback;

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task BuildManifestAsync_GivesABoundTextItsFallback_OrTheSourcesDefault()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);

        await BindTextStepAsync(options, seed, ProjectionBindingKeys.TopScoreToday, "Beat today's best!");
        Result<KioskOfflineManifestDTO> withFallback = await GetService(options).BuildManifestAsync(seed.ClientId);

        await BindTextStepAsync(options, seed, ProjectionBindingKeys.TopScoreToday, "");
        Result<KioskOfflineManifestDTO> withoutFallback = await GetService(options).BuildManifestAsync(seed.ClientId);

        Assert.AreEqual("Beat today's best!", withFallback.Data!.Displays.Single().Steps.First(x => x.StepId == seed.TextStepId).Text);
        Assert.AreEqual(
            ProjectionBindingCatalogue.Find(ProjectionBindingKeys.TopScoreToday)!.DefaultFallback,
            withoutFallback.Data!.Displays.Single().Steps.First(x => x.StepId == seed.TextStepId).Text);
    }

    [TestMethod]
    public async Task ManifestVersion_OfABoundStep_DoesNotChangeBetweenSyncs()
    {
        var options = GetInMemoryOptions();
        Seed seed = await SeedAsync(options);
        await BindTextStepAsync(options, seed, ProjectionBindingKeys.LastGameWinner, "Who's next?");

        // Live data never goes into the manifest, so new scores between syncs can't change it.
        string first = (await GetService(options).BuildManifestAsync(seed.ClientId)).Data!.ManifestVersion;
        string second = (await GetService(options).BuildManifestAsync(seed.ClientId)).Data!.ManifestVersion;

        Assert.AreEqual(first, second);
    }
}
