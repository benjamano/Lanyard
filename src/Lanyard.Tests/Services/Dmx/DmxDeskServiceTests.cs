using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

// The desk facade is the only tenancy check between a signed-in user and the DMX singletons.
[TestClass]
public class DmxDeskServiceTests
{
    private const int MyCompany = 1;
    private const int OtherCompany = 2;

    private sealed class Harness
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public Mock<IDmxService> Dmx { get; } = new();
        public Mock<IDmxSceneRunnerService> Runner { get; } = new();

        public DmxDeskService Service(int companyId = MyCompany)
        {
            Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(Options, new FixedTenantProvider(companyId)));

            return new DmxDeskService(factory.Object, Dmx.Object, Runner.Object, NullLogger<DmxDeskService>.Instance);
        }

        public async Task<Guid> SeedClientAsync(int companyId)
        {
            await using ApplicationDbContext ctx = new(Options);
            Client client = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Kiosk", CreateDate = DateTime.UtcNow };
            ctx.Clients.Add(client);
            await ctx.SaveChangesAsync();
            return client.Id;
        }

        public async Task<Guid> SeedSceneAsync(Guid clientId, int companyId)
        {
            await using ApplicationDbContext ctx = new(Options);
            DmxScene scene = new() { Id = Guid.NewGuid(), CompanyId = companyId, ClientId = clientId, Name = "Scene", IsActive = true, CreateByUserId = "seed" };
            ctx.DmxScenes.Add(scene);
            await ctx.SaveChangesAsync();
            return scene.Id;
        }
    }

    [TestMethod]
    public async Task AnotherCompanysKiosk_IsRefusedForEveryOperation()
    {
        Harness harness = new();
        Guid theirs = await harness.SeedClientAsync(OtherCompany);
        Guid theirScene = await harness.SeedSceneAsync(theirs, OtherCompany);
        DmxDeskService desk = harness.Service(MyCompany);

        Assert.IsFalse((await desk.GetChannelsAsync(theirs)).IsSuccess);
        Assert.IsFalse((await desk.SetChannelsAsync(theirs, [new DmxChannel { Address = 1, Value = 255 }])).IsSuccess);
        Assert.IsFalse((await desk.SubscribeToChannelsAsync(theirs, _ => { })).IsSuccess);
        Assert.IsFalse((await desk.StartSceneAsync(theirs, theirScene)).IsSuccess);
        Assert.IsFalse((await desk.StopSceneAsync(theirs, theirScene)).IsSuccess);
        Assert.IsFalse((await desk.StopAllScenesAsync(theirs)).IsSuccess);
        Assert.IsFalse((await desk.GetRunningSceneIdsAsync(theirs)).IsSuccess);
        Assert.IsFalse((await desk.SubscribeToScenesAsync(theirs, new DmxSceneEventHandlers(_ => { }, _ => { }, (_, _) => { }, (_, _) => { }))).IsSuccess);

        harness.Dmx.VerifyNoOtherCalls();
        harness.Runner.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task OwnKiosk_IsPassedThroughToTheEngine()
    {
        Harness harness = new();
        Guid mine = await harness.SeedClientAsync(MyCompany);
        List<DmxChannel> channels = [new DmxChannel { Address = 1, Value = 255 }];

        Result<bool> result = await harness.Service(MyCompany).SetChannelsAsync(mine, channels);

        Assert.IsTrue(result.IsSuccess, result.Error);
        harness.Dmx.Verify(d => d.UpdateChannelValuesAsync(mine, channels), Times.Once);
    }

    [TestMethod]
    public async Task StartSceneAsync_SceneOnADifferentKioskOfMine_IsRefused()
    {
        Harness harness = new();
        Guid kioskA = await harness.SeedClientAsync(MyCompany);
        Guid kioskB = await harness.SeedClientAsync(MyCompany);
        Guid sceneOnB = await harness.SeedSceneAsync(kioskB, MyCompany);

        Result<bool> result = await harness.Service(MyCompany).StartSceneAsync(kioskA, sceneOnB);

        Assert.IsFalse(result.IsSuccess);
        harness.Runner.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task StartSceneAsync_OwnScene_StartsIt()
    {
        Harness harness = new();
        Guid mine = await harness.SeedClientAsync(MyCompany);
        Guid scene = await harness.SeedSceneAsync(mine, MyCompany);
        harness.Runner.Setup(r => r.StartSceneAsync(mine, scene, null)).ReturnsAsync(Result<bool>.Ok(true));

        Result<bool> result = await harness.Service(MyCompany).StartSceneAsync(mine, scene);

        Assert.IsTrue(result.IsSuccess, result.Error);
        harness.Runner.Verify(r => r.StartSceneAsync(mine, scene, null), Times.Once);
    }

    [TestMethod]
    public async Task SubscribeToScenesAsync_OnlyRelaysThisKiosksEvents_UntilDisposed()
    {
        Harness harness = new();
        Guid mine = await harness.SeedClientAsync(MyCompany);
        DmxDeskService desk = harness.Service(MyCompany);

        List<Guid> started = [];
        List<string> failures = [];
        Result<IDisposable> subscription = await desk.SubscribeToScenesAsync(mine,
            new DmxSceneEventHandlers(started.Add, _ => { }, (_, _) => { }, (_, reason) => failures.Add(reason)));

        Assert.IsTrue(subscription.IsSuccess, subscription.Error);

        Guid myScene = Guid.NewGuid();
        harness.Runner.Raise(r => r.OnSceneStarted += null, Guid.NewGuid(), Guid.NewGuid());
        harness.Runner.Raise(r => r.OnSceneStarted += null, mine, myScene);
        harness.Runner.Raise(r => r.OnSceneFailed += null, mine, myScene, "boom");

        subscription.Data!.Dispose();
        harness.Runner.Raise(r => r.OnSceneStarted += null, mine, Guid.NewGuid());

        CollectionAssert.AreEqual(new[] { myScene }, started);
        CollectionAssert.AreEqual(new[] { "boom" }, failures);
    }
}
