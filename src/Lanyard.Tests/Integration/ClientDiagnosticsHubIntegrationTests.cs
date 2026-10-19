using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The diagnostics hub methods are untyped strings on both sides, and a mismatch silently drops
// the call. A unit test with a mocked IHubContext can't catch that, so this connects a real
// SignalR client to the real /websocket hub and drives the real service end to end. The test
// registers its handler with the same ClientHubMethods constant the kiosk uses, so the kiosk's
// side is pinned to the same value at compile time.
[TestClass]
public class ClientDiagnosticsHubIntegrationTests
{
    private const string SharedSecret = "integration-test-shared-secret";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
    }

    private static HubConnection CreateKioskConnection(WebApplicationFactory<Program> factory, string token)
    {
        Uri hubUrl = new(factory.Server.BaseAddress, "websocket?dmxBatch=1");

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // TestServer has no real sockets; long polling runs over its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    private static async Task WaitUntilConnectedAsync(WebApplicationFactory<Program> factory, Guid clientId)
    {
        // OnConnectedAsync (which records the connection on the client row) can finish
        // just after StartAsync returns.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            Result<bool> connected = await scope.ServiceProvider.GetRequiredService<IClientService>().IsClientConnectedAsync(clientId);

            if (connected.IsSuccess && connected.Data)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The test kiosk never showed as connected.");
    }

    private static async Task<Result<ClientLogTailCapture>> RequestLogTailAsync(WebApplicationFactory<Program> factory, Guid clientId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IClientDiagnosticsService>().RequestLogTailAsync(clientId, "Integration Test");
    }

    [TestMethod]
    public async Task RequestLogTail_RoundTripsThroughTheRealHub()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(factory, clientId);
        await using HubConnection kiosk = CreateKioskConnection(factory, token);

        ClientLogTailRequestDTO? receivedRequest = null;

        kiosk.On<ClientLogTailRequestDTO, ClientLogTailDTO>(ClientHubMethods.RequestLogTail, request =>
        {
            receivedRequest = request;

            return new ClientLogTailDTO
            {
                Lines =
                [
                    new ClientLogLineDTO { TimestampUtc = DateTime.UtcNow, Level = "Information", Category = "Lanyard.Client.Test", Message = "Kiosk started" },
                    new ClientLogLineDTO { TimestampUtc = DateTime.UtcNow, Level = "Error", Category = "Lanyard.Client.Test", Message = $"Rejected with {SharedSecret}" }
                ],
                ClientVersion = "1.0.41",
                CapturedAtUtc = DateTime.UtcNow
            };
        });

        await kiosk.StartAsync();
        await WaitUntilConnectedAsync(factory, clientId);

        Result<ClientLogTailCapture> result = await RequestLogTailAsync(factory, clientId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(receivedRequest);
        Assert.AreEqual(ClientDiagnosticsService.LogTailLines, receivedRequest.MaxLines);
        Assert.HasCount(2, result.Data!.LogTail.Lines);
        Assert.AreEqual("1.0.41", result.Data.LogTail.ClientVersion);
        Assert.AreEqual("Rejected with [REDACTED]", result.Data.LogTail.Lines[1].Message);
    }

    [TestMethod]
    public async Task RequestLogTail_ExplainsWhenTheKioskHasNoHandler()
    {
        // Reproduces a kiosk on a client version from before remote diagnostics: connected, but
        // with nothing registered for RequestLogTail.
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(factory, clientId);
        await using HubConnection kiosk = CreateKioskConnection(factory, token);

        await kiosk.StartAsync();
        await WaitUntilConnectedAsync(factory, clientId);

        Result<ClientLogTailCapture> result = await RequestLogTailAsync(factory, clientId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "older Lanyard Client");
    }

    [TestMethod]
    public async Task RequestScreenshots_RoundTripsAFullSizeImageThroughTheRealHub()
    {
        // The largest JPEG a kiosk will send, as base64 in a JSON message, must still fit the
        // hub's 256 KB limit; a reply over it would drop the kiosk's connection.
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(factory, clientId);
        await using HubConnection kiosk = CreateKioskConnection(factory, token);

        byte[] jpeg = new byte[ClientDiagnosticsService.ScreenshotMaxBytes];
        Random.Shared.NextBytes(jpeg);
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;

        ClientScreenshotRequestDTO? receivedRequest = null;

        kiosk.On<List<ClientProjectionWindowDTO>>(ClientHubMethods.ListProjectionWindows, () =>
        [
            new ClientProjectionWindowDTO { DisplayKey = "DISPLAY1", DisplayIndex = 0, ProgramId = Guid.NewGuid(), IsLoaded = true }
        ]);

        kiosk.On<ClientScreenshotRequestDTO, ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow, request =>
        {
            receivedRequest = request;

            return new ClientScreenshotDTO
            {
                DisplayKey = request.DisplayKey,
                ImageJpeg = jpeg,
                Width = 1280,
                Height = 720,
                ClientVersion = "1.0.43",
                CapturedAtUtc = DateTime.UtcNow
            };
        });

        await kiosk.StartAsync();
        await WaitUntilConnectedAsync(factory, clientId);

        using IServiceScope scope = factory.Services.CreateScope();
        Result<ClientScreenshotCapture> result = await scope.ServiceProvider.GetRequiredService<IClientDiagnosticsService>()
            .RequestScreenshotsAsync(clientId, "Integration Test");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("DISPLAY1", receivedRequest?.DisplayKey);
        ClientScreenSnapshot screen = result.Data!.Screens.Single();
        Assert.IsNull(screen.Error);
        CollectionAssert.AreEqual(jpeg, screen.ImageJpeg);
        Assert.AreEqual("1.0.43", result.Data.ClientVersion);
    }

    [TestMethod]
    public async Task RequestScreenshots_ExplainsWhenTheKioskHasNoHandler()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(factory, clientId);
        await using HubConnection kiosk = CreateKioskConnection(factory, token);

        await kiosk.StartAsync();
        await WaitUntilConnectedAsync(factory, clientId);

        using IServiceScope scope = factory.Services.CreateScope();
        Result<ClientScreenshotCapture> result = await scope.ServiceProvider.GetRequiredService<IClientDiagnosticsService>()
            .RequestScreenshotsAsync(clientId, "Integration Test");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "older Lanyard Client");
    }
}
