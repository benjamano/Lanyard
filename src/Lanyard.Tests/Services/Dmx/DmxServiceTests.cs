using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxServiceTests
{
    private const string ConnectionId = "conn-1";

    private sealed class Harness
    {
        public Mock<ISingleClientProxy> ClientProxy { get; } = new();
        public Mock<IClientService> ClientService { get; } = new();
        public Mock<IHubContext<SignalRControlHub>> HubContext { get; } = new();
        public DmxService Service { get; }

        public Harness(string? connectionId = ConnectionId)
        {
            Mock<IHubContext<SignalRControlHub>> hubContext = HubContext;
            hubContext.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns(ClientProxy.Object);

            ClientService
                .Setup(c => c.GetClientCurrentConnectionIdAsync(It.IsAny<Guid>()))
                .ReturnsAsync(Result<string?>.Ok(connectionId));

            Mock<IServiceProvider> serviceProvider = new();
            serviceProvider.Setup(sp => sp.GetService(typeof(IClientService))).Returns(ClientService.Object);

            Mock<IServiceScope> scope = new();
            scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);

            Mock<IServiceScopeFactory> scopeFactory = new();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            Service = new DmxService(
                Mock.Of<ISystemDbContextFactory>(),
                hubContext.Object,
                Mock.Of<ILogger<DmxService>>(),
                scopeFactory.Object);
        }
    }

    private static List<DmxChannel> Batch(params (int address, byte value)[] channels) =>
        channels.Select(c => new DmxChannel { Address = c.address, Value = c.value }).ToList();

    private static void VerifyBatchSends(Harness harness, int times) =>
        harness.ClientProxy.Verify(
            p => p.SendCoreAsync(DmxService.ReceiveDmxChannelValuesMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    private static IReadOnlyList<DmxChannel> SentBatch(Harness harness, int index) =>
        (IReadOnlyList<DmxChannel>)((object?[])harness.ClientProxy.Invocations
            .Where(i => (string)i.Arguments[0] == DmxService.ReceiveDmxChannelValuesMethod)
            .ElementAt(index).Arguments[1])[0]!;

    [TestMethod]
    public async Task UpdateChannelValuesAsync_SendsOneHubMessageAndNotifiesOnceForTheWholeBatch()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        List<DmxChannel> batch = Batch((1, 255), (2, 128), (3, 0));

        int eventCount = 0;
        IReadOnlyList<DmxChannel>? received = null;
        using IDisposable _ = harness.Service.SubscribeToClient(clientId, channels => { eventCount++; received = channels; });

        await harness.Service.UpdateChannelValuesAsync(clientId, batch);

        Assert.AreEqual(1, eventCount);
        Assert.AreEqual(3, received!.Count);

        harness.ClientService.Verify(c => c.GetClientCurrentConnectionIdAsync(clientId), Times.Once);
        VerifyBatchSends(harness, 1);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, SentBatch(harness, 0).Select(c => c.Address).ToArray());
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_WritesInsideTheWindow_AreCoalescedLatestValueWins()
    {
        Harness harness = new();
        harness.Service.CoalesceWindow = TimeSpan.FromHours(1);
        Guid clientId = Guid.NewGuid();

        // The first write after a quiet spell goes straight out...
        await harness.Service.UpdateChannelValue(clientId, 5, 10);

        // ...the rest wait for the flush, which sends only the latest value per channel.
        await harness.Service.UpdateChannelValue(clientId, 5, 20);
        await harness.Service.UpdateChannelValue(clientId, 5, 30);
        await harness.Service.UpdateChannelValue(clientId, 6, 40);

        VerifyBatchSends(harness, 1);

        await harness.Service.FlushPendingAsync();

        VerifyBatchSends(harness, 2);
        IReadOnlyList<DmxChannel> flushed = SentBatch(harness, 1);
        Assert.AreEqual(2, flushed.Count);
        Assert.AreEqual((byte)30, flushed.Single(c => c.Address == 5).Value);
        Assert.AreEqual((byte)40, flushed.Single(c => c.Address == 6).Value);

        // Nothing left over.
        await harness.Service.FlushPendingAsync();
        VerifyBatchSends(harness, 2);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_CoalescedWrites_AreFlushedByTheBackgroundLoop()
    {
        Harness harness = new();
        harness.Service.CoalesceWindow = TimeSpan.FromMilliseconds(20);
        Guid clientId = Guid.NewGuid();

        await harness.Service.UpdateChannelValue(clientId, 1, 1);
        await harness.Service.UpdateChannelValue(clientId, 1, 2);

        for (int i = 0; i < 100 && harness.ClientProxy.Invocations.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        VerifyBatchSends(harness, 2);
        Assert.AreEqual((byte)2, SentBatch(harness, 1).Single().Value);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_OutOfRangeAddresses_AreDropped()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();

        await harness.Service.UpdateChannelValuesAsync(clientId, Batch((0, 1), (513, 1), (-4, 1), (512, 9)));

        VerifyBatchSends(harness, 1);
        Assert.AreEqual(512, SentBatch(harness, 0).Single().Address);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_EmptyClientId_DoesNothing()
    {
        Harness harness = new();

        await harness.Service.UpdateChannelValuesAsync(Guid.Empty, Batch((1, 1)));

        harness.ClientService.VerifyNoOtherCalls();
        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_KioskWithoutBatchSupport_GetsOneLegacyMessagePerChannel()
    {
        Harness harness = new();
        harness.Service.SetConnectionSupportsBatch(ConnectionId, false);

        await harness.Service.UpdateChannelValuesAsync(Guid.NewGuid(), Batch((1, 255), (2, 128)));

        harness.ClientProxy.Verify(
            p => p.SendCoreAsync(DmxService.LegacyReceiveDmxChannelValueMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        VerifyBatchSends(harness, 0);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_ForgottenLegacyConnection_GoesBackToBatches()
    {
        Harness harness = new();
        harness.Service.SetConnectionSupportsBatch(ConnectionId, false);
        harness.Service.ForgetConnection(ConnectionId);

        await harness.Service.UpdateChannelValuesAsync(Guid.NewGuid(), Batch((1, 255), (2, 128)));

        VerifyBatchSends(harness, 1);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_UpdatesServerSideUniverse()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();

        await harness.Service.UpdateChannelValuesAsync(clientId, Batch((10, 200), (11, 100)));

        Result<IEnumerable<DmxChannel>> channels = await harness.Service.GetDmxChannelsAsync(clientId);
        Dictionary<int, byte> byAddress = channels.Data!.ToDictionary(c => c.Address, c => c.Value);

        Assert.AreEqual(512, byAddress.Count);
        Assert.AreEqual((byte)200, byAddress[10]);
        Assert.AreEqual((byte)100, byAddress[11]);
        Assert.AreEqual((byte)0, byAddress[12]);
    }

    [TestMethod]
    public async Task GetDmxChannelsAsync_ReturnsACopy_NotALiveView()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();

        await harness.Service.UpdateChannelValue(clientId, 1, 50);
        Result<IEnumerable<DmxChannel>> snapshot = await harness.Service.GetDmxChannelsAsync(clientId);

        harness.Service.SetChannelValue(clientId, 1, 99);

        Assert.AreEqual((byte)50, snapshot.Data!.Single(c => c.Address == 1).Value);
    }

    [TestMethod]
    public async Task GetDmxChannelsAsync_UnknownClient_IsAllZeros()
    {
        Harness harness = new();

        Result<IEnumerable<DmxChannel>> channels = await harness.Service.GetDmxChannelsAsync(Guid.NewGuid());

        Assert.AreEqual(512, channels.Data!.Count());
        Assert.IsTrue(channels.Data!.All(c => c.Value == 0));
    }

    [TestMethod]
    public async Task SubscribeToClient_OnlyHearsItsOwnKiosk_AndStopsWhenDisposed()
    {
        Harness harness = new();
        Guid mine = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        int heard = 0;

        IDisposable subscription = harness.Service.SubscribeToClient(mine, _ => heard++);

        await harness.Service.UpdateChannelValue(other, 1, 1);
        await harness.Service.UpdateChannelValue(mine, 1, 1);
        subscription.Dispose();
        subscription.Dispose();
        await harness.Service.UpdateChannelValue(mine, 2, 1);

        Assert.AreEqual(1, heard);
    }

    [TestMethod]
    public async Task ThrowingSubscriber_DoesNotStopOtherSubscribersOrTheKioskSend()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        int heard = 0;

        using IDisposable bad = harness.Service.SubscribeToClient(clientId, _ => throw new InvalidOperationException("boom"));
        using IDisposable good = harness.Service.SubscribeToClient(clientId, _ => heard++);

        await harness.Service.UpdateChannelValue(clientId, 1, 1);

        Assert.AreEqual(1, heard);
        VerifyBatchSends(harness, 1);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_EmptyBatch_DoesNothing()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        int eventCount = 0;
        using IDisposable _ = harness.Service.SubscribeToClient(clientId, _ => eventCount++);

        await harness.Service.UpdateChannelValuesAsync(clientId, []);

        Assert.AreEqual(0, eventCount);
        harness.ClientService.VerifyNoOtherCalls();
        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_KioskOffline_StillUpdatesStateAndNotifiesButSendsNothing()
    {
        Harness harness = new(connectionId: null);
        Guid clientId = Guid.NewGuid();
        int eventCount = 0;
        using IDisposable _ = harness.Service.SubscribeToClient(clientId, _ => eventCount++);

        await harness.Service.UpdateChannelValuesAsync(clientId, Batch((1, 9)));

        Assert.AreEqual(1, eventCount);
        Result<IEnumerable<DmxChannel>> channels = await harness.Service.GetDmxChannelsAsync(clientId);
        Assert.AreEqual((byte)9, channels.Data!.Single(c => c.Address == 1).Value);
        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task KioskOffline_IsNotLookedUpAgainForEveryFrame()
    {
        Harness harness = new(connectionId: null);
        harness.Service.CoalesceWindow = TimeSpan.Zero;
        Guid clientId = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            await harness.Service.UpdateChannelValue(clientId, 1, (byte)i);
        }

        harness.ClientService.Verify(c => c.GetClientCurrentConnectionIdAsync(clientId), Times.Once);
    }

    [TestMethod]
    public async Task RegisteredConnection_IsUsedWithoutADatabaseLookup()
    {
        Harness harness = new(connectionId: null);
        Guid clientId = Guid.NewGuid();

        await harness.Service.RegisterConnectionAsync(clientId, "conn-live");
        await harness.Service.UpdateChannelValue(clientId, 1, 1);

        harness.ClientService.Verify(c => c.GetClientCurrentConnectionIdAsync(It.IsAny<Guid>()), Times.Never);
        harness.HubContext.Verify(h => h.Clients.Client("conn-live"), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task RegisterConnectionAsync_KioskTheServerHasBeenDriving_GetsItsUniverseBack()
    {
        Harness harness = new();
        harness.Service.CoalesceWindow = TimeSpan.Zero;
        Guid clientId = Guid.NewGuid();

        await harness.Service.UpdateChannelValue(clientId, 3, 77);
        harness.ClientProxy.Invocations.Clear();

        // The kiosk restarted (all-zero frame) and connected again.
        harness.Service.ForgetConnection(ConnectionId);
        await harness.Service.RegisterConnectionAsync(clientId, ConnectionId);

        VerifyBatchSends(harness, 1);
        IReadOnlyList<DmxChannel> universe = SentBatch(harness, 0);
        Assert.AreEqual(512, universe.Count);
        Assert.AreEqual((byte)77, universe.Single(c => c.Address == 3).Value);
    }

    [TestMethod]
    public async Task RegisterConnectionAsync_KioskTheServerNeverDrove_IsLeftAlone()
    {
        Harness harness = new();

        await harness.Service.RegisterConnectionAsync(Guid.NewGuid(), ConnectionId);

        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UpdateChannelValue_IsABatchOfOne()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();

        IReadOnlyList<DmxChannel>? received = null;
        using IDisposable _ = harness.Service.SubscribeToClient(clientId, channels => received = channels);

        await harness.Service.UpdateChannelValue(clientId, 7, 42);

        Assert.IsNotNull(received);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(7, received[0].Address);
        Assert.AreEqual((byte)42, received[0].Value);

        VerifyBatchSends(harness, 1);
    }

    [TestMethod]
    public void SetChannelValue_IngestNotifiesBatchOfOne_WithoutSendingToKiosk()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        IReadOnlyList<DmxChannel>? received = null;
        using IDisposable _ = harness.Service.SubscribeToClient(clientId, channels => received = channels);

        harness.Service.SetChannelValue(clientId, 3, 77);

        Assert.IsNotNull(received);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(3, received[0].Address);
        Assert.AreEqual((byte)77, received[0].Value);
        harness.ClientService.VerifyNoOtherCalls();
        harness.ClientProxy.VerifyNoOtherCalls();
    }
}
