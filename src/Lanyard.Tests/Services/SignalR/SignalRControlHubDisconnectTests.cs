using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.SignalR;

/// <summary>
/// Covers OnDisconnectedAsync's guard against clearing a client's live laser-game status when
/// the disconnecting socket has already been superseded by a newer reconnect. Without this
/// guard, a stale connection's disconnect event firing after the client has already reconnected
/// wipes out the status the newer connection just published - reproduced live by connecting two
/// overlapping SignalR clients against a running dev server during manual verification.
/// </summary>
[TestClass]
public class SignalRControlHubDisconnectTests
{
    private const string StaleConnectionId = "stale-connection";
    private const string CurrentConnectionId = "current-connection";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static IDbContextFactory<ApplicationDbContext> GetFactory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    private static SignalRControlHub BuildHub(
        Guid clientId,
        string disconnectingConnectionId,
        string currentMostRecentConnectionId,
        out ILaserGameStatusStore statusStore)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        IDbContextFactory<ApplicationDbContext> factory = GetFactory(options);

        Client client = new()
        {
            Id = clientId,
            Name = "Test Kiosk",
            MostRecentConnectionId = currentMostRecentConnectionId
        };

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientIdFromConnectionIdAsync(disconnectingConnectionId))
            .ReturnsAsync(Result<Guid>.Ok(clientId));
        clientServiceMock.Setup(x => x.GetClientFromIdAsync(clientId))
            .ReturnsAsync(Result<Client?>.Ok(client));
        clientServiceMock.Setup(x => x.UpdateClientAsync(It.IsAny<Client>()))
            .ReturnsAsync((Client c) => Result<Client?>.Ok(c));

        statusStore = new LaserGameStatusStore();

        MusicPlayerService playerService = new(
            Mock.Of<IHubContext<SignalRControlHub>>(),
            factory,
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MusicPlayerService>.Instance);

        AutomationEngineService automationEngineService = new(
            factory, [], NullLogger<AutomationEngineService>.Instance);

        SignalRProjectionControlHubEvents hubEvents = new(Mock.Of<IServiceScopeFactory>());

        Mock<HubCallerContext> hubCallerContextMock = new();
        hubCallerContextMock.SetupGet(x => x.ConnectionId).Returns(disconnectingConnectionId);

        return new SignalRControlHub(
            NullLogger<SignalRControlHub>.Instance,
            playerService,
            clientServiceMock.Object,
            statusStore,
            hubEvents,
            automationEngineService,
            Mock.Of<IDmxClientService>(),
            Mock.Of<IClientZoneScoreboardService>(),
            Mock.Of<IVideoStreamTokenService>(),
            Mock.Of<IGameResultService>(),
            Mock.Of<IClientSecretValidator>())
        {
            Context = hubCallerContextMock.Object,
            Groups = Mock.Of<IGroupManager>()
        };
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_DoesNotClearStatus_WhenConnectionHasAlreadyBeenSuperseded()
    {
        Guid clientId = Guid.NewGuid();

        // The client has already reconnected on CurrentConnectionId by the time
        // StaleConnectionId's disconnect event fires - the exact race a flaky reconnect
        // produces.
        SignalRControlHub hub = BuildHub(clientId, StaleConnectionId, CurrentConnectionId, out ILaserGameStatusStore statusStore);

        statusStore.UpdateStatus(clientId, new LaserGameStatusDTO
        {
            ClientId = clientId,
            Status = GameStatus.NotStarted,
            TimeRemainingSeconds = 599,
            TotalTimeSeconds = 600
        });

        await hub.OnDisconnectedAsync(null);

        Assert.IsTrue(
            statusStore.TryGetStatus(clientId, out LaserGameStatusDTO? status),
            "The stale connection's disconnect should not have cleared the status published by the newer connection.");
        Assert.AreEqual(600, status!.TotalTimeSeconds);
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_ClearsStatus_WhenConnectionIsStillCurrent()
    {
        Guid clientId = Guid.NewGuid();

        // The disconnecting connection is still the client's one and only connection - a
        // genuine disconnect, so the status should be cleared.
        SignalRControlHub hub = BuildHub(clientId, CurrentConnectionId, CurrentConnectionId, out ILaserGameStatusStore statusStore);

        statusStore.UpdateStatus(clientId, new LaserGameStatusDTO
        {
            ClientId = clientId,
            Status = GameStatus.NotStarted,
            TimeRemainingSeconds = 599,
            TotalTimeSeconds = 600
        });

        await hub.OnDisconnectedAsync(null);

        Assert.IsFalse(
            statusStore.TryGetStatus(clientId, out _),
            "Disconnecting the client's actual current connection should clear its status.");
    }
}
