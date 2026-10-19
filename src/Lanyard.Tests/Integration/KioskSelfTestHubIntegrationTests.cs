using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Integration;

// RunSelfTest is an untyped string on both sides, and a mismatch silently drops the call, so this
// drives the real service through the real /websocket hub to a SignalR client registered with the
// same ClientHubMethods constant the kiosk uses.
[TestClass]
public class KioskSelfTestHubIntegrationTests
{
    private const string SharedSecret = "integration-test-shared-secret";

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
    }

    private static HubConnection CreateKioskConnection(WebApplicationFactory<Program> factory, Guid clientId)
    {
        Uri hubUrl = new(factory.Server.BaseAddress, $"websocket?clientId={clientId}&secret={Uri.EscapeDataString(SharedSecret)}&dmxBatch=1");

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
    }

    private static async Task<Client> WaitUntilConnectedAsync(WebApplicationFactory<Program> factory, Guid clientId)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            IClientService clients = scope.ServiceProvider.GetRequiredService<IClientService>();
            Result<bool> connected = await clients.IsClientConnectedAsync(clientId);

            if (connected.IsSuccess && connected.Data)
            {
                return (await clients.GetClientFromIdAsync(clientId)).Data!;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The test kiosk never showed as connected.");
        return null!;
    }

    // The test host has no signed-in user, so its scopes are the system tenant the service refuses.
    // Stand in an Admin of the kiosk's company; everything else is the real container's.
    private static async Task<Result<KioskSelfTestRunView>> RunAsAdminAsync(WebApplicationFactory<Program> factory, Client client, bool flashDmx)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Mock<ISecurityService> security = new();
        security.Setup(s => s.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(true);
        security.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("integration-admin"));

        KioskSelfTestService service = ActivatorUtilities.CreateInstance<KioskSelfTestService>(
            scope.ServiceProvider,
            (ITenantContext)new KioskSelfTestServiceTests.TestTenant(client.CompanyId),
            security.Object);

        return await service.RunAsync(client.Id, flashDmx);
    }

    [TestMethod]
    public async Task RunSelfTest_RoundTripsThroughTheRealHub_AndIsStored()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        await using HubConnection kiosk = CreateKioskConnection(factory, clientId);

        KioskSelfTestRequestDTO? received = null;

        kiosk.On<KioskSelfTestRequestDTO, KioskSelfTestResultDTO>(ClientHubMethods.RunSelfTest, request =>
        {
            received = request;

            return new KioskSelfTestResultDTO
            {
                RunId = request.RunId,
                ClientVersion = "1.0.42",
                StartedUtc = DateTime.UtcNow,
                FinishedUtc = DateTime.UtcNow,
                Checks =
                [
                    SelfTestRules.Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Pass, null, "Played a one-second beep."),
                    SelfTestRules.Check(SelfTestCheckKeys.DmxFlash, SelfTestOutcome.Skipped, SelfTestCodes.OutputLive, "Not flashed because the lights are in use.")
                ]
            };
        });

        await kiosk.StartAsync();
        Client client = await WaitUntilConnectedAsync(factory, clientId);

        Result<KioskSelfTestRunView> result = await RunAsAdminAsync(factory, client, flashDmx: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(received);
        Assert.IsTrue(received.FlashDmx);
        Assert.AreEqual(ClientSelfTestStatus.Completed, result.Data!.Status);
        Assert.AreEqual(1, result.Data.Count(SelfTestOutcome.Skipped));
        StringAssert.Contains(result.Data.Groups.Single(g => g.Group == SelfTestCheckKeys.GroupDmx).Checks.Single().Hint, "Stop the lighting scene");

        using IServiceScope scope = factory.Services.CreateScope();
        Mock<ISecurityService> security = new();
        security.Setup(s => s.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(true);
        KioskSelfTestService reader = ActivatorUtilities.CreateInstance<KioskSelfTestService>(
            scope.ServiceProvider, (ITenantContext)new KioskSelfTestServiceTests.TestTenant(client.CompanyId), security.Object);

        Result<List<KioskSelfTestRunView>> history = await reader.GetHistoryAsync(clientId);
        Assert.AreEqual(result.Data.Id, history.Data!.Single().Id);
    }

    [TestMethod]
    public async Task RunSelfTest_ExplainsWhenTheKioskHasNoHandler()
    {
        // A kiosk on a client from before the self-test: connected, nothing registered.
        await using WebApplicationFactory<Program> factory = CreateFactory();
        Guid clientId = Guid.NewGuid();
        await using HubConnection kiosk = CreateKioskConnection(factory, clientId);

        await kiosk.StartAsync();
        Client client = await WaitUntilConnectedAsync(factory, clientId);

        Result<KioskSelfTestRunView> result = await RunAsAdminAsync(factory, client, flashDmx: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClientSelfTestStatus.KioskTooOld, result.Data!.Status);
        StringAssert.Contains(result.Data.Error, "older Lanyard Client");
    }
}
