using Lanyard.Application.Services;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ProjectionProgramServiceBindingTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ProjectionProgramService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new ProjectionProgramService(factoryMock.Object, Mock.Of<IClientService>());
    }

    private sealed record Seeded(Guid ProgramId, Guid StepId, Guid TextParameterId, Guid VideoStepId, Guid VolumeParameterId);

    private static async Task<Seeded> SeedAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Guid programId = Guid.NewGuid();
        Guid showTextId = Guid.NewGuid();
        Guid playVideoId = Guid.NewGuid();
        Guid textParameterId = Guid.NewGuid();
        Guid volumeParameterId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        Guid videoStepId = Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);

        ctx.ProjectionProgramStepTemplates.AddRange(
            new ProjectionProgramStepTemplate { Id = showTextId, Name = ProjectionTemplateNames.ShowText, IsActive = true },
            new ProjectionProgramStepTemplate { Id = playVideoId, Name = ProjectionTemplateNames.PlayVideo, IsActive = true });

        ctx.ProjectionProgramStepTemplateParameters.AddRange(
            new ProjectionProgramStepTemplateParameter { Id = textParameterId, TemplateId = showTextId, Name = ProjectionParameterNames.Text, DataType = "String", IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = volumeParameterId, TemplateId = playVideoId, Name = ProjectionParameterNames.Volume, DataType = "String", IsActive = true });

        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = programId, Name = "Arena loop", IsActive = true });

        ctx.ProjectionProgramSteps.AddRange(
            new ProjectionProgramStep
            {
                Id = stepId, ProjectionProgramId = programId, TemplateId = showTextId, SortOrder = 0, IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = textParameterId, Value = "Welcome!" }]
            },
            new ProjectionProgramStep
            {
                Id = videoStepId, ProjectionProgramId = programId, TemplateId = playVideoId, SortOrder = 1, IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ParameterId = volumeParameterId, Value = "40" }]
            });

        await ctx.SaveChangesAsync();

        return new Seeded(programId, stepId, textParameterId, videoStepId, volumeParameterId);
    }

    // What the editor sends back: detached copies of the steps with the edited values.
    private static ProjectionProgramStep EditedStep(Guid programId, Guid stepId, Guid parameterId, string? value, string? bindingKey, int sortOrder = 0) => new()
    {
        Id = stepId,
        ProjectionProgramId = programId,
        SortOrder = sortOrder,
        IsActive = true,
        ParameterValues = [new ProjectionProgramParameterValue { ParameterId = parameterId, ProjectionProgramStepId = stepId, Value = value, BindingKey = bindingKey }]
    };

    private static async Task<ProjectionProgramParameterValue> LoadValueAsync(DbContextOptions<ApplicationDbContext> options, Guid stepId)
    {
        await using ApplicationDbContext ctx = new(options);

        return await ctx.Set<ProjectionProgramParameterValue>().AsNoTracking().SingleAsync(x => x.ProjectionProgramStepId == stepId);
    }

    [TestMethod]
    public async Task Save_KeepsTheBindingAndItsFallback_ThenClearsItBackToFixed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);
        ProjectionProgramService service = GetService(options);

        Result<bool> bound = await service.SaveProjectionProgramStepsAsync([EditedStep(seed.ProgramId, seed.StepId, seed.TextParameterId, "Who's next?", ProjectionBindingKeys.LastGameWinner)]);

        Assert.IsTrue(bound.IsSuccess, bound.Error);
        ProjectionProgramParameterValue saved = await LoadValueAsync(options, seed.StepId);
        Assert.AreEqual(ProjectionBindingKeys.LastGameWinner, saved.BindingKey);
        Assert.AreEqual("Who's next?", saved.Value);

        Result<bool> cleared = await service.SaveProjectionProgramStepsAsync([EditedStep(seed.ProgramId, seed.StepId, seed.TextParameterId, "Hello", bindingKey: " ")]);

        Assert.IsTrue(cleared.IsSuccess, cleared.Error);
        Assert.IsNull((await LoadValueAsync(options, seed.StepId)).BindingKey, "A blank key means a fixed value again.");
    }

    [TestMethod]
    public async Task Save_RejectsAnUnknownDataSource()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([EditedStep(seed.ProgramId, seed.StepId, seed.TextParameterId, "x", "game.made-up")]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Unknown data source");
        Assert.IsNull((await LoadValueAsync(options, seed.StepId)).BindingKey);
    }

    [TestMethod]
    public async Task Save_RejectsABindingOnAParameterThatCantShowIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync(
            [EditedStep(seed.ProgramId, seed.VideoStepId, seed.VolumeParameterId, "40", ProjectionBindingKeys.TopScoreToday, sortOrder: 1)]);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Live data can only be used for a Show Text step's text.", result.Error);
    }
}
