using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

// Grand master, blackout, and the saved universe that survives a server restart.
[TestClass]
public class DmxOutputStateTests
{
    private const string ConnectionId = "conn-1";

    // Built-in RGB + Dimmer mode: 4 channels, R, G, B, then the dimmer.
    private static readonly Guid RgbDimmerMode = Guid.Parse("6f1d6a8e-1c1b-4c55-9a0e-000000000103");

    private sealed class Rig
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public Guid ClientId { get; } = Guid.NewGuid();

        public List<IReadOnlyList<DmxChannel>> Sent { get; } = [];
        public List<byte[]> Frames { get; } = [];

        public Rig()
        {
            using ApplicationDbContext ctx = new(Options);
            ctx.Database.EnsureCreated(); // seeds the built-in fixture definitions
            ctx.Clients.Add(new Client { Id = ClientId, CompanyId = 1, Name = "Kiosk", CreateDate = DateTime.UtcNow });
            ctx.SaveChanges();
        }

        public async Task AddFixtureAsync(string name, int startChannel, Guid modeId)
        {
            await using ApplicationDbContext ctx = new(Options);
            ctx.DmxFixtures.Add(new DmxFixture { Id = Guid.NewGuid(), CompanyId = 1, ClientId = ClientId, Name = name, StartChannel = startChannel, FixtureModeId = modeId, IsActive = true, CreateByUserId = "seed" });
            await ctx.SaveChangesAsync();
        }

        // A mode with pan + tilt + red and no intensity, at the given start.
        public async Task AddMovingHeadAsync(int startChannel)
        {
            await using ApplicationDbContext ctx = new(Options);
            Guid definitionId = Guid.NewGuid();
            Guid modeId = Guid.NewGuid();

            ctx.DmxFixtureDefinitions.Add(new DmxFixtureDefinition { Id = definitionId, CompanyId = 1, Manufacturer = "Test", Model = "Head", Source = DmxFixtureDefinitionSource.Custom });
            ctx.DmxFixtureModes.Add(new DmxFixtureMode { Id = modeId, DefinitionId = definitionId, Name = "3ch", ChannelCount = 3 });
            ctx.DmxFixtureModeChannels.AddRange(
                new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = modeId, Offset = 0, Label = "Pan", Role = DmxChannelRole.Pan },
                new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = modeId, Offset = 1, Label = "Tilt", Role = DmxChannelRole.Tilt },
                new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = modeId, Offset = 2, Label = "Red", Role = DmxChannelRole.Red });
            ctx.DmxFixtures.Add(new DmxFixture { Id = Guid.NewGuid(), CompanyId = 1, ClientId = ClientId, Name = "Head", StartChannel = startChannel, FixtureModeId = modeId, IsActive = true, CreateByUserId = "seed" });
            await ctx.SaveChangesAsync();
        }

        // A fresh engine on the same database: what a server restart looks like.
        public DmxService Engine(bool kioskTakesFrames = false)
        {
            Mock<ISystemDbContextFactory> factory = new();
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ApplicationDbContext(Options));

            Mock<ISingleClientProxy> proxy = new();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback((string method, object?[] args, CancellationToken _) =>
                {
                    if (method == DmxService.ReceiveDmxChannelValuesMethod)
                    {
                        Sent.Add((IReadOnlyList<DmxChannel>)args[0]!);
                    }
                    else if (method == Lanyard.Shared.Dmx.DmxReceiveMethods.Frame)
                    {
                        Frames.Add((byte[])args[0]!);
                    }
                })
                .Returns(Task.CompletedTask);

            Mock<IHubContext<SignalRControlHub>> hub = new();
            hub.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns(proxy.Object);

            Mock<IClientService> clients = new();
            clients.Setup(c => c.GetClientCurrentConnectionIdAsync(It.IsAny<Guid>())).ReturnsAsync(Result<string?>.Ok(ConnectionId));

            Mock<IServiceProvider> services = new();
            services.Setup(sp => sp.GetService(typeof(IClientService))).Returns(clients.Object);
            Mock<IServiceScope> scope = new();
            scope.SetupGet(s => s.ServiceProvider).Returns(services.Object);
            Mock<IServiceScopeFactory> scopes = new();
            scopes.Setup(f => f.CreateScope()).Returns(scope.Object);

            DmxService engine = new(factory.Object, hub.Object, NullLogger<DmxService>.Instance, scopes.Object) { CoalesceWindow = TimeSpan.Zero };
            engine.SetConnectionSupportsFrame(ConnectionId, kioskTakesFrames);

            return engine;
        }

        // What the kiosk's lights show now: every send applied in order.
        public byte[] KioskUniverse()
        {
            byte[] universe = new byte[513];

            foreach (byte[] frame in Frames)
            {
                Array.Copy(frame, 0, universe, 1, frame.Length);
            }

            foreach (IReadOnlyList<DmxChannel> batch in Sent)
            {
                foreach (DmxChannel channel in batch)
                {
                    universe[channel.Address] = channel.Value;
                }
            }

            return universe;
        }
    }

    private static List<DmxChannel> Batch(params (int address, byte value)[] channels) =>
        channels.Select(c => new DmxChannel { Address = c.address, Value = c.value }).ToList();

    [TestMethod]
    public async Task GrandMaster_ScalesIntensityAndUnpatched_LeavesColourAndPositionAlone()
    {
        Rig rig = new();
        await rig.AddFixtureAsync("Wash", 1, RgbDimmerMode); // 1-3 = RGB, 4 = dimmer
        await rig.AddMovingHeadAsync(10);                    // 10 pan, 11 tilt, 12 red (no intensity)
        DmxService engine = rig.Engine();

        await engine.UpdateChannelValuesAsync(rig.ClientId, Batch((1, 200), (4, 200), (10, 200), (11, 200), (12, 200), (100, 200)));
        await engine.SetGrandMasterAsync(rig.ClientId, 128);
        await engine.FlushPendingAsync();

        byte[] lights = rig.KioskUniverse();
        Assert.AreEqual((byte)100, lights[4], "Intensity is scaled");
        Assert.AreEqual((byte)200, lights[1], "Colour of a fixture with an intensity channel isn't");
        Assert.AreEqual((byte)200, lights[10], "Pan never moves with the master");
        Assert.AreEqual((byte)200, lights[11], "Tilt never moves with the master");
        Assert.AreEqual((byte)100, lights[12], "An emitter is the brightness of a fixture without intensity");
        Assert.AreEqual((byte)100, lights[100], "Unpatched channels are treated as dimmers");

        // The desk still sees the levels as programmed.
        Result<IEnumerable<DmxChannel>> programmed = await engine.GetDmxChannelsAsync(rig.ClientId);
        Assert.AreEqual((byte)200, programmed.Data!.Single(c => c.Address == 4).Value);
    }

    [TestMethod]
    public async Task Blackout_DarkensDimmableChannels_AndReleasingRestoresThemExactly()
    {
        Rig rig = new();
        await rig.AddMovingHeadAsync(10);
        DmxService engine = rig.Engine();

        await engine.UpdateChannelValuesAsync(rig.ClientId, Batch((1, 77), (10, 50), (12, 255)));
        await engine.SetBlackoutAsync(rig.ClientId, true);
        await engine.FlushPendingAsync();

        byte[] dark = rig.KioskUniverse();
        Assert.AreEqual((byte)0, dark[1]);
        Assert.AreEqual((byte)0, dark[12]);
        Assert.AreEqual((byte)50, dark[10], "Blackout doesn't swing the heads");

        // Moved while blacked out: stays dark, but the new level is what comes back.
        await engine.UpdateChannelValue(rig.ClientId, 1, 90);
        await engine.FlushPendingAsync();
        Assert.AreEqual((byte)0, rig.KioskUniverse()[1]);

        await engine.SetBlackoutAsync(rig.ClientId, false);
        await engine.FlushPendingAsync();

        byte[] restored = rig.KioskUniverse();
        Assert.AreEqual((byte)90, restored[1]);
        Assert.AreEqual((byte)255, restored[12]);
    }

    [TestMethod]
    public async Task OutputChange_IsReportedToSubscribers()
    {
        Rig rig = new();
        DmxService engine = rig.Engine();
        List<DmxOutputState> heard = [];
        using IDisposable _ = engine.SubscribeToClient(rig.ClientId, _ => { }, heard.Add);

        await engine.SetGrandMasterAsync(rig.ClientId, 10);
        await engine.SetGrandMasterAsync(rig.ClientId, 10); // no change, no event
        await engine.SetBlackoutAsync(rig.ClientId, true);

        CollectionAssert.AreEqual(new[] { new DmxOutputState(10, false), new DmxOutputState(10, true) }, heard);
        Assert.AreEqual(new DmxOutputState(10, true), await engine.GetOutputStateAsync(rig.ClientId));
    }

    [TestMethod]
    public async Task SavedState_SurvivesARestart_AndIsSentToTheKioskWhenItConnects()
    {
        Rig rig = new();
        DmxService before = rig.Engine();

        await before.UpdateChannelValuesAsync(rig.ClientId, Batch((1, 11), (512, 22)));
        await before.SetGrandMasterAsync(rig.ClientId, 255);
        await before.SetBlackoutAsync(rig.ClientId, true);
        await before.PersistDirtyAsync();

        // Server restarts; the kiosk reconnects.
        rig.Sent.Clear();
        DmxService after = rig.Engine(kioskTakesFrames: true);
        await after.SetBlackoutAsync(rig.ClientId, false);
        rig.Sent.Clear();
        rig.Frames.Clear();
        await after.RegisterConnectionAsync(rig.ClientId, ConnectionId);

        Assert.AreEqual(1, rig.Frames.Count, "One 512-byte frame, not a batch");
        Assert.AreEqual((byte)11, rig.Frames[0][0]);
        Assert.AreEqual((byte)22, rig.Frames[0][511]);

        Result<IEnumerable<DmxChannel>> desk = await after.GetDmxChannelsAsync(rig.ClientId);
        Assert.AreEqual((byte)11, desk.Data!.Single(c => c.Address == 1).Value);
    }

    [TestMethod]
    public async Task SavedBlackout_IsStillOnAfterARestart()
    {
        Rig rig = new();
        DmxService before = rig.Engine();
        await before.UpdateChannelValue(rig.ClientId, 1, 200);
        await before.SetBlackoutAsync(rig.ClientId, true);
        await before.PersistDirtyAsync();

        DmxService after = rig.Engine();

        Assert.AreEqual(new DmxOutputState(255, true), await after.GetOutputStateAsync(rig.ClientId));
    }

    [TestMethod]
    public async Task PersistDirtyAsync_OnlyWritesKiosksThatChanged()
    {
        Rig rig = new();
        DmxService engine = rig.Engine();

        await engine.PersistDirtyAsync();
        await using (ApplicationDbContext ctx = new(rig.Options))
        {
            Assert.AreEqual(0, await ctx.DmxClientOutputStates.CountAsync(), "Nothing changed, nothing written");
        }

        await engine.UpdateChannelValue(rig.ClientId, 5, 50);
        await engine.PersistDirtyAsync();

        DateTime firstWrite;
        await using (ApplicationDbContext ctx = new(rig.Options))
        {
            DmxClientOutputState row = await ctx.DmxClientOutputStates.SingleAsync();
            Assert.AreEqual((byte)50, row.Values[4]);
            firstWrite = row.UpdatedUtc;
        }

        await Task.Delay(20);
        await engine.PersistDirtyAsync();

        await using (ApplicationDbContext ctx = new(rig.Options))
        {
            Assert.AreEqual(firstWrite, (await ctx.DmxClientOutputStates.SingleAsync()).UpdatedUtc, "Unchanged since: not rewritten");
        }
    }

    [TestMethod]
    public async Task KioskTheServerNeverDrove_IsLeftAloneOnConnect_EvenAfterARestart()
    {
        Rig rig = new();
        DmxService engine = rig.Engine();

        await engine.RegisterConnectionAsync(rig.ClientId, ConnectionId);

        Assert.AreEqual(0, rig.Sent.Count + rig.Frames.Count);
    }

    [TestMethod]
    public async Task RefreshPatchAsync_PatchingAHeadStopsTheMasterScalingItsPan()
    {
        Rig rig = new();
        DmxService engine = rig.Engine();

        await engine.UpdateChannelValue(rig.ClientId, 10, 200);
        await engine.SetGrandMasterAsync(rig.ClientId, 0);
        await engine.FlushPendingAsync();
        Assert.AreEqual((byte)0, rig.KioskUniverse()[10], "Unpatched: a dimmer");

        await rig.AddMovingHeadAsync(10);
        await engine.RefreshPatchAsync(rig.ClientId);
        await engine.FlushPendingAsync();

        Assert.AreEqual((byte)200, rig.KioskUniverse()[10], "Now it's a pan channel, so the master lets go of it");
    }
}
