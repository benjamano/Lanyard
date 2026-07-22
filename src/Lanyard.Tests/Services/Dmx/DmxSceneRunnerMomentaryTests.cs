using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxSceneRunnerMomentaryTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    private static async Task<(Guid clientId, Guid sceneId)> SeedMomentarySceneAsync(
        DbContextOptions<ApplicationDbContext> options, bool isMomentary, bool loop, TimeSpan stepDuration)
    {
        Guid clientId = Guid.NewGuid();
        Guid sceneId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);

        context.DmxScenes.Add(new DmxScene
        {
            Id = sceneId,
            ClientId = clientId,
            Name = "Test momentary scene",
            IsActive = true,
            Loop = loop,
            IsMomentary = isMomentary,
            CreateByUserId = "test-user"
        });

        int[][] stepChannels = [[1, 2], [2, 3]];

        for (int i = 0; i < stepChannels.Length; i++)
        {
            Guid stepId = Guid.NewGuid();

            DmxSceneStep step = new()
            {
                Id = stepId,
                SceneId = sceneId,
                StepNumber = i + 1,
                Name = $"Step {i + 1}",
                Duration = stepDuration,
                CreateByUserId = "test-user"
            };

            foreach (int channel in stepChannels[i])
            {
                step.ChannelValues.Add(new DmxSceneStepChannelValue
                {
                    Id = Guid.NewGuid(),
                    SceneStepId = stepId,
                    ChannelNumber = channel,
                    Value = 255,
                    CreateByUserId = "test-user"
                });
            }

            context.DmxSceneSteps.Add(step);
        }

        await context.SaveChangesAsync();

        return (clientId, sceneId);
    }

    private static async Task WaitForSceneToStopAsync(DmxSceneRunnerService runner, Guid clientId)
    {
        // The loop runs on a background task; poll briefly rather than asserting
        // exact timing, which would be flaky on a busy build machine.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (runner.GetRunningSceneIds(clientId).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task RunScene_Momentary_StopScene_ResetsAllChannelsToZero()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedMomentarySceneAsync(options, isMomentary: true, loop: true, stepDuration: TimeSpan.FromMilliseconds(20));

        Mock<IDmxService> dmxServiceMock = new();

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            dmxServiceMock.Object,
            Mock.Of<IBeatClockService>(),
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await Task.Delay(30);

        Result<bool> stopResult = runner.StopScene(sceneId);
        Assert.IsTrue(stopResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 1, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 2, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 3, 0), Times.Once);
    }

    [TestMethod]
    public async Task RunScene_Momentary_HoldForExpiry_ResetsChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedMomentarySceneAsync(options, isMomentary: true, loop: true, stepDuration: TimeSpan.FromMilliseconds(20));

        Mock<IDmxService> dmxServiceMock = new();

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            dmxServiceMock.Object,
            Mock.Of<IBeatClockService>(),
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId, holdFor: TimeSpan.FromMilliseconds(30));
        Assert.IsTrue(startResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 1, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 2, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 3, 0), Times.Once);
    }

    [TestMethod]
    public async Task RunScene_NonMomentary_StopScene_DoesNotResetChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedMomentarySceneAsync(options, isMomentary: false, loop: true, stepDuration: TimeSpan.FromMilliseconds(20));

        Mock<IDmxService> dmxServiceMock = new();

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            dmxServiceMock.Object,
            Mock.Of<IBeatClockService>(),
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await Task.Delay(30);

        runner.StopScene(sceneId);

        await WaitForSceneToStopAsync(runner, clientId);

        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, It.IsAny<int>(), 0), Times.Never);
    }

    [TestMethod]
    public async Task RunScene_Momentary_NonLooping_CompletesNaturally_ResetsChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedMomentarySceneAsync(options, isMomentary: true, loop: false, stepDuration: TimeSpan.FromMilliseconds(20));

        Mock<IDmxService> dmxServiceMock = new();

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            dmxServiceMock.Object,
            Mock.Of<IBeatClockService>(),
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 1, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 2, 0), Times.Once);
        dmxServiceMock.Verify(d => d.UpdateChannelValue(clientId, 3, 0), Times.Once);
    }
}
