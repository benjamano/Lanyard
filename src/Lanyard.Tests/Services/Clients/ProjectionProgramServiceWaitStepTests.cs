using Lanyard.Application.Services;
using Lanyard.Application.Services.Projection;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

/// <summary>Saving a program with a "Wait for Game" step, and saving values onto existing steps.</summary>
[TestClass]
public class ProjectionProgramServiceWaitStepTests
{
    private sealed record Seeded(Guid ProgramId, Guid TemplateId, Guid EventParameterId, Guid KioskParameterId, Guid SecondsParameterId, Guid GiveUpParameterId, Guid LaserKioskId, Guid OtherCompanyKioskId);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static ProjectionProgramService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new ProjectionProgramService(factoryMock.Object, Mock.Of<IClientService>());
    }

    private static async Task<Seeded> SeedAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Seeded seed = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using ApplicationDbContext ctx = new(options);

        ctx.Clients.AddRange(
            new Client { Id = seed.LaserKioskId, CompanyId = 1, Name = "Arena 1" },
            new Client { Id = seed.OtherCompanyKioskId, CompanyId = 2, Name = "Someone else's arena" });

        ctx.ProjectionProgramStepTemplates.Add(new ProjectionProgramStepTemplate { Id = seed.TemplateId, Name = GameWaitStepNames.Template, IsActive = true });

        ctx.ProjectionProgramStepTemplateParameters.AddRange(
            new ProjectionProgramStepTemplateParameter { Id = seed.EventParameterId, TemplateId = seed.TemplateId, Name = GameWaitStepNames.GameEventParameter, DataType = GameWaitStepNames.GameEventDataType, IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.KioskParameterId, TemplateId = seed.TemplateId, Name = GameWaitStepNames.LaserKioskParameter, DataType = GameWaitStepNames.LaserKioskDataType, IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.SecondsParameterId, TemplateId = seed.TemplateId, Name = GameWaitStepNames.SecondsLeftParameter, DataType = "Integer", IsActive = true },
            new ProjectionProgramStepTemplateParameter { Id = seed.GiveUpParameterId, TemplateId = seed.TemplateId, Name = GameWaitStepNames.GiveUpAfterParameter, DataType = "Integer", IsActive = true });

        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = seed.ProgramId, CompanyId = 1, Name = "Arena loop", IsActive = true });

        await ctx.SaveChangesAsync();

        return seed;
    }

    // A new step as the editor sends it.
    private static ProjectionProgramStep WaitStep(Seeded seed, string? gameEvent, Guid? kioskId, string? secondsLeft = null, string? giveUpAfter = null, Guid? stepId = null)
    {
        Guid id = stepId ?? Guid.NewGuid();

        return new ProjectionProgramStep
        {
            Id = id,
            ProjectionProgramId = seed.ProgramId,
            TemplateId = seed.TemplateId,
            SortOrder = 0,
            IsActive = true,
            ParameterValues =
            [
                new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = id, ParameterId = seed.EventParameterId, Value = gameEvent },
                new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = id, ParameterId = seed.KioskParameterId, Value = kioskId?.ToString() ?? string.Empty },
                new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = id, ParameterId = seed.SecondsParameterId, Value = secondsLeft },
                new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = id, ParameterId = seed.GiveUpParameterId, Value = giveUpAfter }
            ]
        };
    }

    private static async Task<int> StepCountAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        return await ctx.ProjectionProgramSteps.CountAsync();
    }

    [TestMethod]
    public async Task Save_AcceptsAValidWaitStep()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "TimeLeftBelow", seed.LaserKioskId, secondsLeft: "60", giveUpAfter: "20")]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, await StepCountAsync(options));
    }

    [TestMethod]
    public async Task Save_RefusesAWaitStepWithoutAnEvent()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "", seed.LaserKioskId)]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.StartsWith(result.Error, "Step 1 (Wait for Game): Choose which game event");
        Assert.AreEqual(0, await StepCountAsync(options), "Nothing is saved when a step is invalid.");
    }

    [TestMethod]
    public async Task Save_RefusesAWaitStepWithoutAKiosk()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "GameEnds", kioskId: null)]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "laser kiosk");
    }

    [TestMethod]
    public async Task Save_RefusesTimeLeftWithoutSeconds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "TimeLeftBelow", seed.LaserKioskId, secondsLeft: "")]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Seconds Left");
    }

    [TestMethod]
    public async Task Save_RefusesAKioskFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "GameEnds", seed.OtherCompanyKioskId)]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't available");
        Assert.AreEqual(0, await StepCountAsync(options));
    }

    [TestMethod]
    public async Task Save_RefusesAKioskThatDoesNotExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([WaitStep(seed, "GameEnds", Guid.NewGuid())]);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Save_KeepsAValueAddedToAStepThatAlreadyExists()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);
        Guid stepId = Guid.NewGuid();

        // Saved first with only an event, as if the step predated the other parameters.
        ProjectionProgramStep step = WaitStep(seed, "GameEnds", seed.LaserKioskId, stepId: stepId);
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ProjectionProgramSteps.Add(new ProjectionProgramStep
            {
                Id = stepId,
                ProjectionProgramId = seed.ProgramId,
                TemplateId = seed.TemplateId,
                IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = stepId, ParameterId = seed.EventParameterId, Value = "GameEnds" }]
            });
            await ctx.SaveChangesAsync();
        }

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([step]);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext check = new(options);
        List<ProjectionProgramParameterValue> values = await check.Set<ProjectionProgramParameterValue>()
            .AsNoTracking()
            .Where(v => v.ProjectionProgramStepId == stepId)
            .ToListAsync();

        Assert.AreEqual(4, values.Count, "The values added to the existing step must be saved.");
        Assert.AreEqual(seed.LaserKioskId.ToString(), values.Single(v => v.ParameterId == seed.KioskParameterId).Value);
    }

    [TestMethod]
    public async Task Save_RemovesAValueTakenOffAStepThatAlreadyExists()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options);
        Guid stepId = Guid.NewGuid();

        ProjectionProgramStep full = WaitStep(seed, "GameEnds", seed.LaserKioskId, giveUpAfter: "10", stepId: stepId);
        Assert.IsTrue((await GetService(options).SaveProjectionProgramStepsAsync([full])).IsSuccess);

        ProjectionProgramStep trimmed = WaitStep(seed, "GameEnds", seed.LaserKioskId, stepId: stepId);
        trimmed.ParameterValues.RemoveAll(v => v.ParameterId == seed.GiveUpParameterId);

        Result<bool> result = await GetService(options).SaveProjectionProgramStepsAsync([trimmed]);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext check = new(options);
        Assert.IsFalse(await check.Set<ProjectionProgramParameterValue>().AnyAsync(v => v.ProjectionProgramStepId == stepId && v.ParameterId == seed.GiveUpParameterId));
    }
}
