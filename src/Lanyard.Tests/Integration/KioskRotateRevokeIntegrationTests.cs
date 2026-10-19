using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Integration;

// Rotating and revoking a kiosk's credential against a real hub connection: the kiosk has to
// confirm a rotation before its old credential stops, and a revoke cuts a live connection off.
[TestClass]
public class KioskRotateRevokeIntegrationTests
{
    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", "integration-test-kiosk-secret"));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        KioskSecurityService.RotationAcknowledgementTimeout = TimeSpan.FromSeconds(30);
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    // The service as an Admin would use it from the Clients page. Outside a Blazor circuit there's
    // no auth state, so an Admin's is supplied.
    private (IServiceScope Scope, IKioskSecurityService Service) AsAdmin()
    {
        IServiceScope scope = _factory.Services.CreateScope();

        Mock<AuthenticationStateProvider> auth = new();
        auth.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "admin-user"), new Claim(ClaimTypes.Role, "Admin")], "Test"))));

        return (scope, ActivatorUtilities.CreateInstance<KioskSecurityService>(scope.ServiceProvider, auth.Object));
    }

    private async Task<HttpStatusCode> FeedStatusAsync(Guid clientId, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/" + ClientUpdateRoutes.Feed(clientId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        for (int attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(100);
        }

        Assert.IsTrue(condition(), failure);
    }

    private async Task<HubConnection> ConnectAsync(Guid clientId, Func<string?> token)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/websocket"), options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token());
            })
            .Build();

        await connection.StartAsync();

        // OnConnectedAsync marks the connection live only at its end, after StartAsync returns.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            Result<bool> connected = await scope.ServiceProvider.GetRequiredService<Lanyard.Application.Services.IClientService>().IsClientConnectedAsync(clientId);

            if (connected.IsSuccess && connected.Data)
            {
                return connection;
            }

            await Task.Delay(100);
        }

        Assert.Fail("The kiosk never showed as connected.");

        return connection;
    }

    [TestMethod]
    public async Task Rotate_GivesTheKioskANewCredential_AndRetiresTheOld()
    {
        Guid clientId = Guid.NewGuid();
        string oldToken = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);
        string current = oldToken;
        string? received = null;

        await using HubConnection kiosk = await ConnectAsync(clientId, () => current);
        kiosk.On<string, bool>(ClientHubEvents.ReceiveCredential, credential =>
        {
            received = credential;
            current = credential;
            return true;
        });

        (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
        using (scope)
        {
            Result<bool> rotated = await service.RotateAsync(clientId);
            Assert.IsTrue(rotated.IsSuccess, rotated.Error);
        }

        Assert.IsNotNull(received);
        Assert.AreNotEqual(oldToken, received);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await FeedStatusAsync(clientId, oldToken), "The old credential still works.");
        Assert.AreEqual(HttpStatusCode.OK, await FeedStatusAsync(clientId, received));
    }

    [TestMethod]
    public async Task Rotate_KioskThatCannotSave_KeepsItsOldCredential()
    {
        Guid clientId = Guid.NewGuid();
        string oldToken = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        await using HubConnection kiosk = await ConnectAsync(clientId, () => oldToken);
        kiosk.On<string, bool>(ClientHubEvents.ReceiveCredential, _ => false);

        (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
        using (scope)
        {
            Assert.IsFalse((await service.RotateAsync(clientId)).IsSuccess);
        }

        Assert.AreEqual(HttpStatusCode.OK, await FeedStatusAsync(clientId, oldToken));
    }

    [TestMethod]
    public async Task Rotate_OldKioskWithoutTheHandler_IsCalledOff()
    {
        Guid clientId = Guid.NewGuid();
        string oldToken = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        await using HubConnection kiosk = await ConnectAsync(clientId, () => oldToken);

        (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
        using (scope)
        {
            Result<bool> rotated = await service.RotateAsync(clientId);

            Assert.IsFalse(rotated.IsSuccess);
            StringAssert.Contains(rotated.Error, "too old");
        }

        Assert.AreEqual(HttpStatusCode.OK, await FeedStatusAsync(clientId, oldToken));
    }

    [TestMethod]
    public async Task Rotate_RefusesAKioskThatIsNotConnected()
    {
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
        using (scope)
        {
            Assert.IsFalse((await service.RotateAsync(clientId)).IsSuccess);
        }
    }

    [TestMethod]
    public async Task Revoke_CutsOffTheLiveConnection_AndItCannotComeBack()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        HubConnection kiosk = await ConnectAsync(clientId, () => token);

        try
        {
            bool closed = false;
            kiosk.Closed += _ =>
            {
                closed = true;
                return Task.CompletedTask;
            };

            (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
            using (scope)
            {
                Result<bool> revoked = await service.RevokeAsync(clientId, "PC stolen");
                Assert.IsTrue(revoked.IsSuccess, revoked.Error);
            }

            await WaitForAsync(() => closed || kiosk.State != HubConnectionState.Connected, "The revoked kiosk's live connection wasn't closed.");

            await using HubConnection again = KioskTestSupport.CreateCredentialConnection(_factory, token);
            HttpRequestException refused = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => again.StartAsync());
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        finally
        {
            await kiosk.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Revoke_ThenExchangingTheSharedSecret_IsRefused()
    {
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        (IServiceScope scope, IKioskSecurityService service) = AsAdmin();
        using (scope)
        {
            Assert.IsTrue((await service.RevokeAsync(clientId, null)).IsSuccess);
        }

        // A revoked kiosk must be paired again by an Admin, not let back in by the shared secret.
        using HttpRequestMessage exchange = new(HttpMethod.Post, $"/{KioskEnrolmentRoutes.LegacyExchange}?{ClientConnectionQuery.ClientId}={clientId}");
        exchange.Headers.Add(ClientConnectionQuery.SecretHeader, "integration-test-kiosk-secret");

        Assert.AreEqual(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(exchange)).StatusCode);
    }

    [TestMethod]
    public async Task SecurityService_IsAdminOnly()
    {
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        using IServiceScope scope = _factory.Services.CreateScope();

        Mock<AuthenticationStateProvider> manager = new();
        manager.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "manager-user"), new Claim(ClaimTypes.Role, "Manager")], "Test"))));

        KioskSecurityService service = ActivatorUtilities.CreateInstance<KioskSecurityService>(scope.ServiceProvider, manager.Object);

        Assert.IsFalse((await service.RevokeAsync(clientId, null)).IsSuccess);
        Assert.IsFalse((await service.RotateAsync(clientId)).IsSuccess);
        Assert.IsFalse((await service.GetAsync(clientId)).IsSuccess);
    }
}
