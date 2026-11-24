using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class DisplayIdentifyServiceTests
{
    private const string ConnectionId = "connection-1";
    private const int CompanyId = 7;

    private sealed class Harness
    {
        public required DisplayIdentifyService Service { get; init; }
        public required Mock<ISingleClientProxy> Proxy { get; init; }
        public required Mock<IClientService> ClientService { get; init; }
        public required TestClock Clock { get; init; }
        public Guid ClientId { get; init; }

        public DisplayIdentifyRequestDTO? Sent { get; set; }

        public List<ClientAvailableScreenDTO>? Refreshed { get; set; }
    }

    private static IdentifiedDisplayDTO Screen(int index, string name, bool primary = false, bool shown = true)
    {
        return new IdentifiedDisplayDTO { Index = index, DeviceName = name, Width = 1920, Height = 1080, IsPrimary = primary, Shown = shown };
    }

    private static DisplayIdentifyResultDTO Reply(Guid requestId, params IdentifiedDisplayDTO[] displays)
    {
        return new DisplayIdentifyResultDTO
        {
            RequestId = requestId,
            ClientVersion = "1.0.49",
            Displays = displays.Length > 0 ? [.. displays] : [Screen(0, @"\\.\DISPLAY1", primary: true), Screen(1, @"\\.\DISPLAY2")]
        };
    }

    private static ProjectionRunState Running(Guid clientId, int displayIndex, string programName)
    {
        return new ProjectionRunState
        {
            RunId = Guid.NewGuid(),
            ClientId = clientId,
            DisplayIndex = displayIndex,
            ProgramId = Guid.NewGuid(),
            ProgramName = programName,
            Steps = [],
            CurrentStepIndex = 0,
            IsPaused = false,
            IsTemporaryTrigger = true
        };
    }

    private static Harness Build(
        bool connected = true,
        bool isAdmin = true,
        ITenantContext? tenant = null,
        Func<DisplayIdentifyRequestDTO, DisplayIdentifyResultDTO?>? kiosk = null,
        Exception? kioskThrows = null,
        List<ClientAvailableScreen>? storedScreens = null,
        List<ClientProjectionSettings>? settings = null,
        Func<Guid, List<ProjectionRunState>>? running = null,
        string? reportedVersion = "1.0.48")
    {
        Guid clientId = Guid.NewGuid();

        Harness? harness = null;

        Mock<IClientService> clientService = new();
        clientService.Setup(c => c.GetClientFromIdAsync(clientId))
            .ReturnsAsync(Result<Client?>.Ok(new Client { Id = clientId, CompanyId = CompanyId, Name = "Arena PC", MostRecentConnectionId = ConnectionId, ReportedClientVersion = reportedVersion }));
        clientService.Setup(c => c.GetClientFromIdAsync(It.Is<Guid>(id => id != clientId)))
            .ReturnsAsync(Result<Client?>.Ok(null));
        clientService.Setup(c => c.IsClientConnectedAsync(clientId))
            .ReturnsAsync(Result<bool>.Ok(connected));
        clientService.Setup(c => c.GetClientAvailableScreensAsync(clientId))
            .ReturnsAsync(Result<IEnumerable<ClientAvailableScreen>>.Ok(storedScreens ?? []));
        clientService.Setup(c => c.GetClientProjectionSettingsAsync(clientId))
            .ReturnsAsync(Result<IEnumerable<ClientProjectionSettings>>.Ok(settings ?? []));
        clientService.Setup(c => c.SetClientAvailableScreensAsync(clientId, It.IsAny<IEnumerable<ClientAvailableScreenDTO>>()))
            .ReturnsAsync((Guid _, IEnumerable<ClientAvailableScreenDTO> screens) =>
            {
                harness!.Refreshed = screens.ToList();
                return Result<bool>.Ok(true);
            });

        Mock<ISingleClientProxy> proxy = new();
        var invoke = proxy.Setup(p => p.InvokeCoreAsync<DisplayIdentifyResultDTO>(ClientHubMethods.IdentifyDisplays, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()));

        if (kioskThrows != null)
        {
            invoke.ThrowsAsync(kioskThrows);
        }
        else
        {
            invoke.ReturnsAsync((string _, object?[] args, CancellationToken _) =>
            {
                DisplayIdentifyRequestDTO request = (DisplayIdentifyRequestDTO)args[0]!;
                harness!.Sent = request;
                return (kiosk ?? (r => Reply(r.RequestId)))(request)!;
            });
        }

        Mock<IHubClients> hubClients = new();
        hubClients.Setup(c => c.Client(ConnectionId)).Returns(proxy.Object);

        Mock<IHubContext<SignalRControlHub>> hubContext = new();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        Mock<ISecurityService> security = new();
        security.Setup(s => s.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(isAdmin);
        security.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("admin-1"));

        Mock<IProjectionProgramRunnerService> runner = new();
        runner.Setup(r => r.GetRunningStates()).Returns(() => running?.Invoke(clientId) ?? []);

        TestClock clock = new(new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc));

        DisplayIdentifyService service = new(
            clientService.Object,
            hubContext.Object,
            new ClientDiagnosticsStore(clock),
            runner.Object,
            tenant ?? new KioskSelfTestServiceTests.TestTenant(CompanyId),
            security.Object,
            NullLogger<DisplayIdentifyService>.Instance);

        harness = new Harness
        {
            Service = service,
            Proxy = proxy,
            ClientService = clientService,
            Clock = clock,
            ClientId = clientId
        };

        return harness;
    }

    private static void AssertKioskNeverAsked(Harness h)
    {
        h.Proxy.Verify(p => p.InvokeCoreAsync<DisplayIdentifyResultDTO>(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ClientProjectionSettings Setting(int displayIndex, string programName, bool isActive = true, bool programActive = true)
    {
        return new ClientProjectionSettings
        {
            Id = Guid.NewGuid(),
            DisplayIndex = displayIndex,
            IsActive = isActive,
            ProjectionProgram = new ProjectionProgram { Id = Guid.NewGuid(), Name = programName, IsActive = programActive }
        };
    }

    [TestMethod]
    public async Task IdentifyAsync_ReturnsTheKiosksScreens_NumberedFromOne()
    {
        Harness h = Build(settings: [Setting(1, "Arena Loop")]);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        DisplayIdentifyView view = result.Data!;

        Assert.AreEqual(DisplayIdentifyRules.DefaultDurationSeconds, view.DurationSeconds);
        Assert.AreEqual(DisplayIdentifyRules.DefaultDurationSeconds, h.Sent!.DurationSeconds);
        Assert.AreEqual("1.0.49", view.ClientVersion);
        CollectionAssert.AreEqual(new[] { 1, 2 }, view.Displays.Select(x => x.Number).ToArray());
        Assert.IsTrue(view.Displays[0].IsPrimary);
        Assert.IsNull(view.Displays[0].ProgramName);
        Assert.AreEqual("Arena Loop", view.Displays[1].ProgramName);
        Assert.IsFalse(view.AnyChanged);
    }

    [TestMethod]
    public async Task IdentifyAsync_SendsEachScreensProgram_PreferringWhatIsPlayingNow()
    {
        Harness h = Build(
            settings: [Setting(0, "Welcome"), Setting(1, "Arena Loop")],
            running: clientId => [Running(clientId, 1, "Birthday Countdown"), Running(Guid.NewGuid(), 0, "Another kiosk's program")]);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Dictionary<int, string?> sent = h.Sent!.Labels.ToDictionary(x => x.DisplayIndex, x => x.ProgramName);
        Assert.AreEqual("Welcome", sent[0], "Another kiosk's run must not be used");
        Assert.AreEqual("Birthday Countdown", sent[1], "What's playing beats what's set up");
        Assert.AreEqual("Birthday Countdown", result.Data!.Displays[1].ProgramName);
    }

    [TestMethod]
    public async Task IdentifyAsync_IgnoresInactiveSettingsAndPrograms()
    {
        Harness h = Build(settings: [Setting(0, "Removed setting", isActive: false), Setting(1, "Deleted program", programActive: false)]);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsEmpty(h.Sent!.Labels);
        Assert.IsTrue(result.Data!.Displays.All(x => x.ProgramName == null));
    }

    [TestMethod]
    public async Task IdentifyAsync_FlagsScreensThatChangedNumber_AndRefreshesTheStoredList()
    {
        Harness h = Build(storedScreens:
        [
            new ClientAvailableScreen { Id = Guid.NewGuid(), Name = @"\\.\DISPLAY2", Index = 0, IsActive = true },
            new ClientAvailableScreen { Id = Guid.NewGuid(), Name = @"\\.\DISPLAY1", Index = 1, IsActive = true }
        ]);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        DisplayIdentifyView view = result.Data!;

        Assert.IsTrue(view.AnyChanged);
        Assert.IsTrue(view.Displays[0].Changed);
        Assert.AreEqual(@"\\.\DISPLAY2", view.Displays[0].PreviousDeviceName);

        Assert.IsNotNull(h.Refreshed);
        CollectionAssert.AreEqual(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, h.Refreshed.OrderBy(x => x.Index).Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task IdentifyAsync_ScreenStillInPlace_IsNotChanged()
    {
        Harness h = Build(storedScreens: [new ClientAvailableScreen { Id = Guid.NewGuid(), Name = @"\\.\display1", Index = 0, IsActive = true }]);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.Displays[0].Changed, "Device names compare case-insensitively");
        Assert.IsFalse(result.Data.Displays[1].Changed, "A screen Lanyard didn't know about isn't a change");
    }

    [TestMethod]
    public async Task IdentifyAsync_ScreenThatCouldNotShow_IsReportedWithItsError()
    {
        IdentifiedDisplayDTO broken = Screen(1, @"\\.\DISPLAY2", shown: false);
        broken.Error = "The window couldn't be created.";
        Harness h = Build(kiosk: r => Reply(r.RequestId, Screen(0, @"\\.\DISPLAY1"), broken));

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.Displays[1].Shown);
        Assert.AreEqual("The window couldn't be created.", result.Data.Displays[1].Error);
    }

    [TestMethod]
    public async Task IdentifyAsync_NotAnAdmin_IsRefused()
    {
        Harness h = Build(isAdmin: false);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Only an Admin");
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task IdentifyAsync_SystemCaller_IsRefused()
    {
        Harness h = Build(tenant: new KioskSelfTestServiceTests.TestTenant(null, isSystem: true));

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task IdentifyAsync_KioskTheCallerCantSee_IsNotFound()
    {
        Harness h = Build();

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(Guid.NewGuid());

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Couldn't find");
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task IdentifyAsync_OfflineKiosk_IsRefused()
    {
        Harness h = Build(connected: false);

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't connected");
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task IdentifyAsync_KioskWithoutTheHandler_SaysItsTooOld()
    {
        Harness h = Build(kioskThrows: new HubException("Client didn't provide a result."), reportedVersion: "1.0.48");

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Lanyard Client 1.0.48");
        StringAssert.Contains(result.Error, DisplayIdentifyRules.MinimumClientVersion);
        Assert.IsNull(h.Refreshed, "Nothing to refresh without a reply");
    }

    [TestMethod]
    public async Task IdentifyAsync_KioskTimesOut_SaysSo()
    {
        Harness h = Build(kioskThrows: new OperationCanceledException());

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "didn't reply");
    }

    [TestMethod]
    public async Task IdentifyAsync_ReplyForAnotherRequest_IsRejected()
    {
        Harness h = Build(kiosk: _ => Reply(Guid.NewGuid()));

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "different request");
        Assert.IsNull(h.Refreshed);
    }

    [TestMethod]
    public async Task IdentifyAsync_TooManyScreens_IsRejected()
    {
        Harness h = Build(kiosk: r => Reply(r.RequestId, Enumerable.Range(0, DisplayIdentifyRules.MaxDisplays + 1).Select(i => Screen(i, $"D{i}")).ToArray()));

        Result<DisplayIdentifyView> result = await h.Service.IdentifyAsync(h.ClientId);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(h.Refreshed);
    }

    [TestMethod]
    public async Task IdentifyAsync_DoubleClick_IsAbsorbed_ThenAllowedAgain()
    {
        Harness h = Build();

        Assert.IsTrue((await h.Service.IdentifyAsync(h.ClientId)).IsSuccess);

        Result<DisplayIdentifyView> second = await h.Service.IdentifyAsync(h.ClientId);
        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "a moment ago");

        h.Clock.Advance(ClientDiagnosticsStore.IdentifyCooldown);
        Assert.IsTrue((await h.Service.IdentifyAsync(h.ClientId)).IsSuccess, "Pressing again after the cooldown shows the numbers again");
    }
}
