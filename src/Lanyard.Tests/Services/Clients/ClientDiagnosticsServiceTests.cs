using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ClientDiagnosticsServiceTests
{
    private const string ConnectionId = "connection-1";
    private const string ServerSecret = "server-shared-secret";

    private sealed class Harness
    {
        public required ClientDiagnosticsService Service { get; init; }
        public required Mock<ISingleClientProxy> Proxy { get; init; }
        public required Mock<IClientService> ClientService { get; init; }
        public required ClientDiagnosticsStore Store { get; init; }
        public required TestClock Clock { get; init; }
        public Guid ClientId { get; init; }
    }

    private static Harness Build(bool connected = true, string? connectionId = ConnectionId)
    {
        Guid clientId = Guid.NewGuid();

        Mock<IClientService> clientService = new();
        clientService.Setup(c => c.GetClientFromIdAsync(clientId))
            .ReturnsAsync(Result<Client?>.Ok(new Client { Id = clientId, Name = "Kiosk", MostRecentConnectionId = connectionId }));
        clientService.Setup(c => c.GetClientFromIdAsync(It.Is<Guid>(id => id != clientId)))
            .ReturnsAsync(Result<Client?>.Ok(null));
        clientService.Setup(c => c.IsClientConnectedAsync(clientId))
            .ReturnsAsync(Result<bool>.Ok(connected));

        Mock<ISingleClientProxy> proxy = new();
        proxy.Setup(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ClientLogTailDTO
            {
                Lines = [new ClientLogLineDTO { Level = "Information", Category = "Lanyard.Client", Message = "Hello" }],
                ClientVersion = "1.0.41"
            });

        Mock<IHubClients> hubClients = new();
        hubClients.Setup(c => c.Client(ConnectionId)).Returns(proxy.Object);

        Mock<IHubContext<SignalRControlHub>> hubContext = new();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Clients:SharedSecret"] = ServerSecret })
            .Build();

        TestClock clock = new(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc));
        ClientDiagnosticsStore store = new(clock);

        ClientDiagnosticsService service = new(clientService.Object, hubContext.Object, store, configuration,
            NullLogger<ClientDiagnosticsService>.Instance);

        return new Harness { Service = service, Proxy = proxy, ClientService = clientService, Store = store, Clock = clock, ClientId = clientId };
    }

    [TestMethod]
    public async Task RequestLogTailAsync_ReturnsAndKeepsTheKiosksLog()
    {
        Harness harness = Build();

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Hello", result.Data!.LogTail.Lines.Single().Message);
        Assert.AreEqual("Ben", result.Data.RequestedBy);
        Assert.AreSame(result.Data, harness.Service.GetLatestLogTail(harness.ClientId).Data);
        harness.Proxy.Verify(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail,
            It.Is<object?[]>(args => args.Length == 1
                && ((ClientLogTailRequestDTO)args[0]!).MaxLines == ClientDiagnosticsService.LogTailLines
                && ((ClientLogTailRequestDTO)args[0]!).MaxBytes == ClientDiagnosticsService.LogTailMaxBytes),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_FailsWithoutAskingWhenTheKioskIsOffline()
    {
        // A stale MostRecentConnectionId is still set, which must not count as reachable.
        Harness harness = Build(connected: false);

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("This kiosk isn't connected right now.", result.Error);
        harness.Proxy.Verify(p => p.InvokeCoreAsync<ClientLogTailDTO>(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_FailsForAnUnknownClient()
    {
        Harness harness = Build();

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(Guid.NewGuid(), "Ben");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Couldn't find this kiosk.", result.Error);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_ExplainsWhenTheKioskIsTooOld()
    {
        // An older kiosk has no RequestLogTail handler, so SignalR completes the invocation with
        // an error instead of a result.
        Harness harness = Build();
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HubException("Client didn't provide a result."));

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "older Lanyard Client");
        Assert.IsNull(harness.Service.GetLatestLogTail(harness.ClientId).Data);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_ExplainsWhenTheKioskDoesNotRespond()
    {
        Harness harness = Build();
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "didn't respond");
    }

    [TestMethod]
    public async Task RequestLogTailAsync_FailsOnAnEmptyReply()
    {
        Harness harness = Build();
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientLogTailDTO)null!);

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "empty reply");
    }

    [TestMethod]
    public async Task RequestLogTailAsync_BlocksARepeatWithinTheCooldown()
    {
        Harness harness = Build();

        Result<ClientLogTailCapture> first = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");
        Result<ClientLogTailCapture> second = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        harness.Clock.Advance(ClientDiagnosticsStore.LogTailCooldown);
        Result<ClientLogTailCapture> third = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "a moment ago");
        Assert.IsTrue(third.IsSuccess, third.Error);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_ReleasesTheInFlightMarkAfterAFailure()
    {
        Harness harness = Build();
        harness.Proxy.SetupSequence(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException())
            .ReturnsAsync(new ClientLogTailDTO());

        await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");
        harness.Clock.Advance(ClientDiagnosticsStore.LogTailCooldown);
        Result<ClientLogTailCapture> retry = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsTrue(retry.IsSuccess, retry.Error);
    }

    [TestMethod]
    public async Task RequestLogTailAsync_RedactsTheServersSecretAgain()
    {
        // A kiosk with a stale or missing LANYARD_CLIENT_SHARED_SECRET can't redact the real
        // value itself, so the server must.
        Harness harness = Build();
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientLogTailDTO
            {
                Lines = [new ClientLogLineDTO { Category = "Test", Message = $"Failed with {ServerSecret} in the message" }]
            });

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Failed with [REDACTED] in the message", result.Data!.LogTail.Lines.Single().Message);
    }
}
