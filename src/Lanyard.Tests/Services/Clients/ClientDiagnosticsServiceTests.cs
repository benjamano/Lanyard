using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

    private const string FirstDisplay = @"\\.\DISPLAY1";
    private const string SecondDisplay = @"\\.\DISPLAY2";

    // Enough of a JPEG for the service's signature check.
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private sealed class Harness
    {
        public required ClientDiagnosticsService Service { get; init; }
        public required Mock<ISingleClientProxy> Proxy { get; init; }
        public required Mock<IClientService> ClientService { get; init; }
        public required ClientDiagnosticsStore Store { get; init; }
        public required TestClock Clock { get; init; }
        public Guid ClientId { get; init; }
        public required DbContextOptions<ApplicationDbContext> DbOptions { get; init; }
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

        DbContextOptions<ApplicationDbContext> dbOptions = SchedulingTestHelpers.GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(dbOptions));

        ClientDiagnosticsService service = new(clientService.Object, hubContext.Object, store, factory.Object, configuration,
            NullLogger<ClientDiagnosticsService>.Instance);

        return new Harness
        {
            Service = service,
            Proxy = proxy,
            ClientService = clientService,
            Store = store,
            Clock = clock,
            ClientId = clientId,
            DbOptions = dbOptions
        };
    }

    [TestMethod]
    public async Task RequestLogTailAsync_ReturnsAndKeepsTheKiosksLog()
    {
        Harness harness = Build();

        Result<ClientLogTailCapture> result = await harness.Service.RequestLogTailAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Hello", result.Data!.LogTail.Lines.Single().Message);
        Assert.AreEqual("Ben", result.Data.RequestedBy);
        Assert.AreSame(result.Data, (await harness.Service.GetLatestLogTailAsync(harness.ClientId)).Data);
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
        Assert.IsNull((await harness.Service.GetLatestLogTailAsync(harness.ClientId)).Data);
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

    [TestMethod]
    public async Task GetLatestLogTailAsync_RefusesAKioskTheCallerCannotSee()
    {
        // The store is shared by every company; the tenant-filtered client lookup is what stops
        // one company reading another's capture by its Guid.
        Harness harness = Build();
        Guid otherCompanysClient = Guid.NewGuid();
        harness.Store.SaveLogTail(otherCompanysClient, new ClientLogTailDTO(), "Someone else");

        Result<ClientLogTailCapture?> result = await harness.Service.GetLatestLogTailAsync(otherCompanysClient);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    private static void SetupWindows(Harness harness, params ClientProjectionWindowDTO[] windows)
    {
        harness.Proxy.Setup(p => p.InvokeCoreAsync<List<ClientProjectionWindowDTO>>(ClientHubMethods.ListProjectionWindows, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. windows]);
    }

    private static void SetupCapture(Harness harness, string displayKey, ClientScreenshotDTO reply)
    {
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow,
                It.Is<object?[]>(args => ((ClientScreenshotRequestDTO)args[0]!).DisplayKey == displayKey),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(reply);
    }

    private static async Task<Guid> SeedProgramAsync(Harness harness, string name)
    {
        await using ApplicationDbContext ctx = new(harness.DbOptions);
        ProjectionProgram program = new() { Id = Guid.NewGuid(), Name = name, IsActive = true };
        ctx.ProjectionPrograms.Add(program);
        await ctx.SaveChangesAsync();

        return program.Id;
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_CapturesEachScreenAndKeepsTheResult()
    {
        Harness harness = Build();
        Guid lobby = await SeedProgramAsync(harness, "Lobby loop");
        Guid arena = await SeedProgramAsync(harness, "Arena scores");

        SetupWindows(harness,
            new ClientProjectionWindowDTO { DisplayKey = FirstDisplay, DisplayIndex = 0, ProgramId = lobby, IsLoaded = true },
            new ClientProjectionWindowDTO { DisplayKey = SecondDisplay, DisplayIndex = 1, ProgramId = arena, IsLoaded = true, IsOffline = true });
        SetupCapture(harness, FirstDisplay, new ClientScreenshotDTO { DisplayKey = FirstDisplay, ImageJpeg = Jpeg, Width = 1280, Height = 720, ClientVersion = "1.0.43" });
        SetupCapture(harness, SecondDisplay, new ClientScreenshotDTO { DisplayKey = SecondDisplay, ImageJpeg = Jpeg, Width = 1280, Height = 1024, ClientVersion = "1.0.43" });

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.HasCount(2, result.Data!.Screens);
        Assert.AreEqual("Lobby loop", result.Data.Screens[0].ProgramName);
        Assert.AreEqual("Arena scores", result.Data.Screens[1].ProgramName);
        Assert.IsTrue(result.Data.Screens[1].IsOffline);
        Assert.AreEqual(1024, result.Data.Screens[1].Height);
        Assert.AreEqual("1.0.43", result.Data.ClientVersion);
        Assert.AreEqual("Ben", result.Data.RequestedBy);
        Assert.AreSame(result.Data, (await harness.Service.GetLatestScreenshotsAsync(harness.ClientId)).Data);
        harness.Proxy.Verify(p => p.InvokeCoreAsync<ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow,
            It.Is<object?[]>(args => ((ClientScreenshotRequestDTO)args[0]!).MaxBytes == ClientDiagnosticsService.ScreenshotMaxBytes
                && ((ClientScreenshotRequestDTO)args[0]!).MaxWidth == ClientDiagnosticsService.ScreenshotMaxWidth),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_KeepsTheOtherScreensWhenOneFails()
    {
        Harness harness = Build();
        SetupWindows(harness,
            new ClientProjectionWindowDTO { DisplayKey = FirstDisplay, DisplayIndex = 0, IsLoaded = true },
            new ClientProjectionWindowDTO { DisplayKey = SecondDisplay, DisplayIndex = 1, IsLoaded = true });
        SetupCapture(harness, FirstDisplay, new ClientScreenshotDTO { DisplayKey = FirstDisplay, Error = "The kiosk couldn't capture this screen." });
        SetupCapture(harness, SecondDisplay, new ClientScreenshotDTO { DisplayKey = SecondDisplay, ImageJpeg = Jpeg, Width = 640, Height = 360 });

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.Screens[0].ImageJpeg);
        Assert.AreEqual("The kiosk couldn't capture this screen.", result.Data.Screens[0].Error);
        Assert.IsNotNull(result.Data.Screens[1].ImageJpeg);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_MarksAWindowThatNeverLoadedWithoutAskingForIt()
    {
        Harness harness = Build();
        SetupWindows(harness, new ClientProjectionWindowDTO { DisplayKey = FirstDisplay, DisplayIndex = 0, IsLoaded = false });

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Screens.Single().Error, "failed to load");
        harness.Proxy.Verify(p => p.InvokeCoreAsync<ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_MarksAScreenThatTimesOut()
    {
        Harness harness = Build();
        SetupWindows(harness, new ClientProjectionWindowDTO { DisplayKey = FirstDisplay, DisplayIndex = 0, IsLoaded = true });
        harness.Proxy.Setup(p => p.InvokeCoreAsync<ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data!.Screens.Single().Error, "in time");
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_RefusesAnImageThatIsNotAJpeg()
    {
        // The dialog shows the bytes as a JPEG data URL, so anything else must not get through.
        Harness harness = Build();
        SetupWindows(harness, new ClientProjectionWindowDTO { DisplayKey = FirstDisplay, DisplayIndex = 0, IsLoaded = true });
        SetupCapture(harness, FirstDisplay, new ClientScreenshotDTO { DisplayKey = FirstDisplay, ImageJpeg = "<svg onload=alert(1)>"u8.ToArray() });

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.Screens.Single().ImageJpeg);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_ReturnsNoScreensWhenNothingIsProjected()
    {
        Harness harness = Build();
        SetupWindows(harness);

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty(result.Data!.Screens);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_ExplainsWhenTheKioskIsTooOld()
    {
        Harness harness = Build();
        harness.Proxy.Setup(p => p.InvokeCoreAsync<List<ClientProjectionWindowDTO>>(ClientHubMethods.ListProjectionWindows, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HubException("Client didn't provide a result."));

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "older Lanyard Client without remote screenshots");
        Assert.IsNull((await harness.Service.GetLatestScreenshotsAsync(harness.ClientId)).Data);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_FailsWithoutAskingWhenTheKioskIsOffline()
    {
        Harness harness = Build(connected: false);

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("This kiosk isn't connected right now.", result.Error);
        harness.Proxy.Verify(p => p.InvokeCoreAsync<List<ClientProjectionWindowDTO>>(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_RefusesAKioskTheCallerCannotSee()
    {
        Harness harness = Build();

        Result<ClientScreenshotCapture> result = await harness.Service.RequestScreenshotsAsync(Guid.NewGuid(), "Ben");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Couldn't find this kiosk.", result.Error);
    }

    [TestMethod]
    public async Task RequestScreenshotsAsync_BlocksARepeatWithinTheCooldown()
    {
        Harness harness = Build();
        SetupWindows(harness);

        Result<ClientScreenshotCapture> first = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");
        Result<ClientScreenshotCapture> second = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        harness.Clock.Advance(ClientDiagnosticsStore.ScreenshotCooldown);
        Result<ClientScreenshotCapture> third = await harness.Service.RequestScreenshotsAsync(harness.ClientId, "Ben");

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "a moment ago");
        Assert.IsTrue(third.IsSuccess, third.Error);
    }

    [TestMethod]
    public async Task GetLatestScreenshotsAsync_RefusesAKioskTheCallerCannotSee()
    {
        Harness harness = Build();
        Guid otherCompanysClient = Guid.NewGuid();
        harness.Store.SaveScreenshots(otherCompanysClient, [new ClientScreenSnapshot { ImageJpeg = Jpeg }], null, "Someone else");

        Result<ClientScreenshotCapture?> result = await harness.Service.GetLatestScreenshotsAsync(otherCompanysClient);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
    }
}
