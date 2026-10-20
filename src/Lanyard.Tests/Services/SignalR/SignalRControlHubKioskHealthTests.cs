using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.SignalR;

/// <summary>
/// ReportKioskHealth's return value is the kiosk's only confirmation that a heartbeat was
/// stored, so it must be false whenever the report did not land.
/// </summary>
[TestClass]
public class SignalRControlHubKioskHealthTests
{
    private const string ConnectionId = "kiosk-connection";

    private static SignalRControlHub BuildHub(Mock<IClientService> clientService, Mock<IKioskHealthService> kioskHealthService)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        MusicPlayerService playerService = new(
            Mock.Of<IHubContext<SignalRControlHub>>(),
            factoryMock.Object,
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<MusicPlayerService>.Instance);

        AutomationEngineService automationEngineService = new(
            factoryMock.Object, [], new LaserGameStatusStore(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance);

        Mock<HubCallerContext> hubCallerContextMock = new();
        hubCallerContextMock.Setup(x => x.Items).Returns(new Dictionary<object, object?>());
        hubCallerContextMock.SetupGet(x => x.ConnectionId).Returns(ConnectionId);

        return new SignalRControlHub(
            NullLogger<SignalRControlHub>.Instance,
            playerService,
            clientService.Object,
            new LaserGameStatusStore(),
            new SignalRProjectionControlHubEvents(Mock.Of<IServiceScopeFactory>()),
            automationEngineService,
            Mock.Of<IDmxClientService>(),
            Mock.Of<IClientZoneScoreboardService>(),
            Mock.Of<IVideoStreamTokenService>(),
            Mock.Of<IGameResultService>(),
            Mock.Of<IKioskMediaTokenService>(),
            new KioskConnectionRegistry(),
            kioskHealthService.Object,
            Mock.Of<IAudioClipPlaybackService>())
        {
            Context = hubCallerContextMock.Object
        };
    }

    [TestMethod]
    public async Task ReportKioskHealth_StoresTheReport_ForTheCallingKiosk()
    {
        Guid clientId = Guid.NewGuid();
        KioskHealthReportDTO report = new() { ClientVersion = "1.0.41" };

        Mock<IClientService> clientService = new();
        clientService.Setup(x => x.GetClientIdFromConnectionIdAsync(ConnectionId)).ReturnsAsync(Result<Guid>.Ok(clientId));

        Mock<IKioskHealthService> kioskHealthService = new();
        kioskHealthService.Setup(x => x.RecordReportAsync(clientId, report)).ReturnsAsync(Result<bool>.Ok(true));

        bool stored = await BuildHub(clientService, kioskHealthService).ReportKioskHealth(report);

        Assert.IsTrue(stored);
        kioskHealthService.Verify(x => x.RecordReportAsync(clientId, report), Times.Once);
    }

    [TestMethod]
    public async Task ReportKioskHealth_ReturnsFalse_ForAnUnknownConnection()
    {
        Mock<IClientService> clientService = new();
        clientService.Setup(x => x.GetClientIdFromConnectionIdAsync(ConnectionId)).ReturnsAsync(Result<Guid>.Fail("not found"));

        Mock<IKioskHealthService> kioskHealthService = new();

        bool stored = await BuildHub(clientService, kioskHealthService).ReportKioskHealth(new KioskHealthReportDTO());

        Assert.IsFalse(stored);
        kioskHealthService.Verify(x => x.RecordReportAsync(It.IsAny<Guid>(), It.IsAny<KioskHealthReportDTO>()), Times.Never);
    }

    [TestMethod]
    public async Task ReportKioskHealth_ReturnsFalse_WhenTheReportIsNotStored()
    {
        Guid clientId = Guid.NewGuid();

        Mock<IClientService> clientService = new();
        clientService.Setup(x => x.GetClientIdFromConnectionIdAsync(ConnectionId)).ReturnsAsync(Result<Guid>.Ok(clientId));

        Mock<IKioskHealthService> kioskHealthService = new();
        kioskHealthService.Setup(x => x.RecordReportAsync(clientId, It.IsAny<KioskHealthReportDTO>())).ReturnsAsync(Result<bool>.Fail("db down"));

        Assert.IsFalse(await BuildHub(clientService, kioskHealthService).ReportKioskHealth(new KioskHealthReportDTO()));
    }
}
