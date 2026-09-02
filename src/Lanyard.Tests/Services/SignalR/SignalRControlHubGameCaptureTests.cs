using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
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
/// Covers the InGame -> NotStarted edge detection in UpdateLaserGameStatus.
/// </summary>
/// <remarks>
/// This lives at the hub rather than in GameResultService because the guard has to be there:
/// the kiosk keeps republishing a finished game's scores for the whole idle period afterwards
/// (GameStateService only clears them on the next game start), so anything less than strict edge
/// triggering writes the same game once per heartbeat.
/// </remarks>
[TestClass]
public class SignalRControlHubGameCaptureTests
{
    private const string TestConnectionId = "test-connection";

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
        Mock<IGameResultService> gameResultServiceMock,
        out ILaserGameStatusStore statusStore)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        IDbContextFactory<ApplicationDbContext> factory = GetFactory(options);

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientIdFromConnectionIdAsync(TestConnectionId))
            .ReturnsAsync(Result<Guid>.Ok(clientId));

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
        hubCallerContextMock.SetupGet(x => x.ConnectionId).Returns(TestConnectionId);

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
            gameResultServiceMock.Object,
            Mock.Of<IClientSecretValidator>())
        {
            Context = hubCallerContextMock.Object
        };
    }

    private static LaserGameStatusDTO BuildStatus(GameStatus status, bool withScores = true)
    {
        return new LaserGameStatusDTO
        {
            Status = status,
            TotalTimeSeconds = 600,
            PlayerCount = withScores ? 1 : 0,
            PlayerScores = withScores
                ? [new PlayerScoreDTO { GunId = 1, Score = 500, Accuracy = 40, Team = Team.Red }]
                : []
        };
    }

    [TestMethod]
    public async Task UpdateLaserGameStatus_RecordsGameOnInGameToNotStartedEdge()
    {
        Guid clientId = Guid.NewGuid();
        Mock<IGameResultService> gameResultServiceMock = new();
        gameResultServiceMock
            .Setup(x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        SignalRControlHub hub = BuildHub(clientId, gameResultServiceMock, out _);

        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));

        gameResultServiceMock.Verify(
            x => x.RecordCompletedGameAsync(clientId, It.IsAny<LaserGameStatusDTO>()),
            Times.Once);
    }

    [TestMethod]
    public async Task UpdateLaserGameStatus_RecordsGameWithNonZeroDuration()
    {
        // Regression guard for GameStateService.HandleGameEnded() (client-side, not reachable
        // from this test project): it must fire its edge-triggered publish before zeroing
        // TotalTimeSeconds/TimeRemainingSeconds, otherwise every completed game would be
        // recorded with DurationSeconds = 0. This asserts the hub forwards whatever duration
        // the incoming DTO carries rather than discarding/zeroing it during edge detection.
        Guid clientId = Guid.NewGuid();
        Mock<IGameResultService> gameResultServiceMock = new();
        gameResultServiceMock
            .Setup(x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        SignalRControlHub hub = BuildHub(clientId, gameResultServiceMock, out _);

        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));

        gameResultServiceMock.Verify(
            x => x.RecordCompletedGameAsync(clientId, It.Is<LaserGameStatusDTO>(dto => dto.TotalTimeSeconds > 0)),
            Times.Once);
    }

    [TestMethod]
    public async Task UpdateLaserGameStatus_DoesNotRecordAgainOnRepeatedNotStartedHeartbeats()
    {
        Guid clientId = Guid.NewGuid();
        Mock<IGameResultService> gameResultServiceMock = new();
        gameResultServiceMock
            .Setup(x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        SignalRControlHub hub = BuildHub(clientId, gameResultServiceMock, out _);

        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));

        // The kiosk goes on publishing the same finished scores while the zone sits idle.
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));

        gameResultServiceMock.Verify(
            x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()),
            Times.Once);
    }

    [TestMethod]
    public async Task UpdateLaserGameStatus_DoesNotRecordWhileGameIsStillRunning()
    {
        Guid clientId = Guid.NewGuid();
        Mock<IGameResultService> gameResultServiceMock = new();

        SignalRControlHub hub = BuildHub(clientId, gameResultServiceMock, out _);

        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));

        gameResultServiceMock.Verify(
            x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()),
            Times.Never);
    }

    [TestMethod]
    public async Task UpdateLaserGameStatus_RecordsEachGameSeparatelyAcrossConsecutiveGames()
    {
        Guid clientId = Guid.NewGuid();
        Mock<IGameResultService> gameResultServiceMock = new();
        gameResultServiceMock
            .Setup(x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        SignalRControlHub hub = BuildHub(clientId, gameResultServiceMock, out _);

        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.InGame));
        await hub.UpdateLaserGameStatus(BuildStatus(GameStatus.NotStarted));

        gameResultServiceMock.Verify(
            x => x.RecordCompletedGameAsync(It.IsAny<Guid>(), It.IsAny<LaserGameStatusDTO>()),
            Times.Exactly(2));
    }
}
