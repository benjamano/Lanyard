using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxFadeTests
{
    [TestMethod]
    [DataRow((byte)0, (byte)200, 0.0, (byte)0)]
    [DataRow((byte)0, (byte)200, 0.5, (byte)100)]
    [DataRow((byte)0, (byte)200, 1.0, (byte)200)]
    [DataRow((byte)200, (byte)0, 0.25, (byte)150)]
    [DataRow((byte)0, (byte)255, 2.0, (byte)255)]
    [DataRow((byte)10, (byte)20, -1.0, (byte)10)]
    public void Interpolate_IsLinearAndClamped(byte from, byte to, double progress, byte expected)
    {
        Assert.AreEqual(expected, DmxFade.Interpolate(from, to, progress));
    }

    [TestMethod]
    public void Effective_NeverRunsPastTheStepOrBelowZero()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), DmxFade.Effective(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        Assert.AreEqual(TimeSpan.FromSeconds(2), DmxFade.Effective(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
        Assert.AreEqual(TimeSpan.Zero, DmxFade.Effective(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void Frame_OnlySendsChannelsThatMoved()
    {
        List<(int, byte, byte)> channels = [(1, 0, 255), (2, 50, 50)];

        List<DmxChannel> first = DmxFade.Frame(channels, 0, -1);
        List<DmxChannel> later = DmxFade.Frame(channels, 0.5, 0);

        Assert.AreEqual(2, first.Count, "The first frame sets everything.");
        Assert.AreEqual(1, later.Count, "A channel staying at 50 isn't sent again.");
        Assert.AreEqual(1, later[0].Address);
    }

    private static async Task<(Guid clientId, Guid sceneId)> SeedAsync(DbContextOptions<ApplicationDbContext> options, TimeSpan duration, TimeSpan fade, bool momentary = false)
    {
        Guid clientId = Guid.NewGuid();
        Guid sceneId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);
        ctx.DmxScenes.Add(new DmxScene { Id = sceneId, ClientId = clientId, Name = "Fade", IsActive = true, Loop = false, IsMomentary = momentary, CreateByUserId = "t" });
        DmxSceneStep step = new() { Id = stepId, SceneId = sceneId, StepNumber = 1, Name = "Up", Duration = duration, FadeTime = fade, CreateByUserId = "t" };
        step.ChannelValues.Add(new DmxSceneStepChannelValue { Id = Guid.NewGuid(), SceneStepId = stepId, ChannelNumber = 1, Value = 200, CreateByUserId = "t" });
        ctx.DmxSceneSteps.Add(step);
        await ctx.SaveChangesAsync();

        return (clientId, sceneId);
    }

    private static (DmxSceneRunnerService runner, List<byte> sent) Runner(DbContextOptions<ApplicationDbContext> options, Guid clientId)
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        List<byte> sent = [];
        Mock<IDmxService> dmx = new();
        dmx.Setup(d => d.GetDmxChannelsAsync(clientId))
            .ReturnsAsync(Result<IEnumerable<DmxChannel>>.Ok(Enumerable.Range(1, 512).Select(a => new DmxChannel { Address = a, Value = 0 }).ToList()));
        dmx.Setup(d => d.UpdateChannelValuesAsync(clientId, It.IsAny<IReadOnlyList<DmxChannel>>()))
            .Callback((Guid _, IReadOnlyList<DmxChannel> batch) =>
            {
                lock (sent)
                {
                    sent.AddRange(batch.Where(c => c.Address == 1).Select(c => c.Value));
                }
            })
            .Returns(Task.CompletedTask);

        return (new DmxSceneRunnerService(factory.Object, dmx.Object, Mock.Of<IBeatClockService>(), NullLogger<DmxSceneRunnerService>.Instance), sent);
    }

    private static async Task WaitStoppedAsync(DmxSceneRunnerService runner, Guid clientId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (runner.GetRunningSceneIds(clientId).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task Runner_FadesThroughInBetweenLevels_AndEndsOnTheStep()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        (Guid clientId, Guid sceneId) = await SeedAsync(options, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(300));
        (DmxSceneRunnerService runner, List<byte> sent) = Runner(options, clientId);

        await runner.StartSceneAsync(clientId, sceneId);
        await WaitStoppedAsync(runner, clientId);

        lock (sent)
        {
            Assert.IsTrue(sent.Count >= 4, $"{sent.Count} frames for a 300 ms fade");
            Assert.IsTrue(sent.Any(v => v is > 0 and < 200), "Passes through in-between levels");
            Assert.AreEqual((byte)200, sent[^1], "Ends exactly on the step's level");
            CollectionAssert.AreEqual(sent.OrderBy(v => v).ToList(), sent, "Only ever goes up");
        }
    }

    [TestMethod]
    public async Task Runner_FadeLongerThanTheStep_StillFinishesWithinTheStep()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        (Guid clientId, Guid sceneId) = await SeedAsync(options, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));
        (DmxSceneRunnerService runner, List<byte> sent) = Runner(options, clientId);

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        await runner.StartSceneAsync(clientId, sceneId);
        await WaitStoppedAsync(runner, clientId);

        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Took {elapsed.Elapsed}: the fade wasn't clamped to the 200 ms step");

        lock (sent)
        {
            Assert.AreEqual((byte)200, sent[^1]);
        }
    }

    [TestMethod]
    public async Task Runner_StoppedMidFade_StopsCleanly_AndAMomentarySceneStillResets()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        (Guid clientId, Guid sceneId) = await SeedAsync(options, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), momentary: true);
        (DmxSceneRunnerService runner, List<byte> sent) = Runner(options, clientId);

        await runner.StartSceneAsync(clientId, sceneId);
        await Task.Delay(300);
        runner.StopScene(sceneId);
        await WaitStoppedAsync(runner, clientId);

        Assert.AreEqual(0, runner.GetRunningSceneIds(clientId).Count);

        lock (sent)
        {
            Assert.IsTrue(sent.Take(sent.Count - 1).All(v => v < 200), "Didn't reach the step's level before it was stopped");
            Assert.AreEqual((byte)0, sent[^1], "Momentary reset after the fade was cut short");
        }
    }
}
