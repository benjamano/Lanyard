using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
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
        public DmxService Service { get; }

        public Harness(string? connectionId = ConnectionId)
        {
            Mock<IHubContext<SignalRControlHub>> hubContext = new();
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
                Mock.Of<IDbContextFactory<ApplicationDbContext>>(),
                hubContext.Object,
                Mock.Of<ILogger<DmxService>>(),
                scopeFactory.Object);
        }
    }

    private static List<DmxChannel> Batch(params (int address, byte value)[] channels) =>
        channels.Select(c => new DmxChannel { Address = c.address, Value = c.value }).ToList();

    [TestMethod]
    public async Task UpdateChannelValuesAsync_SendsOneHubMessageAndRaisesOneEventForTheWholeBatch()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        List<DmxChannel> batch = Batch((1, 255), (2, 128), (3, 0));

        int eventCount = 0;
        IReadOnlyList<DmxChannel>? received = null;
        harness.Service.OnChannelValuesChanged += (_, channels) => { eventCount++; received = channels; };

        await harness.Service.UpdateChannelValuesAsync(clientId, batch);

        Assert.AreEqual(1, eventCount);
        Assert.AreEqual(3, received!.Count);

        harness.ClientService.Verify(c => c.GetClientCurrentConnectionIdAsync(clientId), Times.Once);
        harness.ClientProxy.Verify(
            p => p.SendCoreAsync(
                DmxService.ReceiveDmxChannelValuesMethod,
                It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], batch)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.ClientProxy.VerifyNoOtherCalls();
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
    public async Task UpdateChannelValue_IsABatchOfOne()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();

        IReadOnlyList<DmxChannel>? received = null;
        harness.Service.OnChannelValuesChanged += (_, channels) => received = channels;

        await harness.Service.UpdateChannelValue(clientId, 7, 42);

        Assert.IsNotNull(received);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(7, received[0].Address);
        Assert.AreEqual((byte)42, received[0].Value);

        harness.ClientProxy.Verify(
            p => p.SendCoreAsync(DmxService.ReceiveDmxChannelValuesMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_EmptyBatch_DoesNothing()
    {
        Harness harness = new();
        int eventCount = 0;
        harness.Service.OnChannelValuesChanged += (_, _) => eventCount++;

        await harness.Service.UpdateChannelValuesAsync(Guid.NewGuid(), []);

        Assert.AreEqual(0, eventCount);
        harness.ClientService.VerifyNoOtherCalls();
        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UpdateChannelValuesAsync_KioskOffline_StillUpdatesStateAndRaisesEventButSendsNothing()
    {
        Harness harness = new(connectionId: null);
        Guid clientId = Guid.NewGuid();
        int eventCount = 0;
        harness.Service.OnChannelValuesChanged += (_, _) => eventCount++;

        await harness.Service.UpdateChannelValuesAsync(clientId, Batch((1, 9)));

        Assert.AreEqual(1, eventCount);
        Result<IEnumerable<DmxChannel>> channels = await harness.Service.GetDmxChannelsAsync(clientId);
        Assert.AreEqual((byte)9, channels.Data!.Single(c => c.Address == 1).Value);
        harness.ClientProxy.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void SetChannelValue_IngestRaisesBatchOfOne_WithoutSendingToKiosk()
    {
        Harness harness = new();
        Guid clientId = Guid.NewGuid();
        IReadOnlyList<DmxChannel>? received = null;
        harness.Service.OnChannelValuesChanged += (_, channels) => received = channels;

        harness.Service.SetChannelValue(clientId, 3, 77);

        Assert.IsNotNull(received);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(3, received[0].Address);
        Assert.AreEqual((byte)77, received[0].Value);
        harness.ClientService.VerifyNoOtherCalls();
        harness.ClientProxy.VerifyNoOtherCalls();
    }
}
