using Lanyard.Application.Services;
using Lanyard.Application.Services.ClientReleases;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lanyard.Tests.Integration;

// The kiosk update path end to end: the feed endpoint behind the shared-secret check, and the
// "Update now" hub call reaching a handler registered under the same shared constant the kiosk uses.
[TestClass]
public class ClientUpdatesIntegrationTests
{
    private const string SharedSecret = "integration-test-kiosk-secret";

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _baseFactory.GitHubReleases.Releases.Add(new GitHubClientRelease(
            "LanyardClient-1.0.41", "1.0.41", false, DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["releases.win.json"] = "https://github.example/1.0.41/releases.win.json",
                ["LanyardClient-1.0.41-full.nupkg"] = "https://github.example/1.0.41/LanyardClient-1.0.41-full.nupkg"
            }));
        _baseFactory.GitHubReleases.Files["https://github.example/1.0.41/releases.win.json"] =
            """{"Assets":[{"PackageId":"LanyardClient","Version":"1.0.41","Type":"Full","FileName":"LanyardClient-1.0.41-full.nupkg","SHA1":"A","SHA256":"B","Size":1}]}""";

        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<HubConnection> ConnectKioskAsync(Guid clientId, Action<HubConnection> register)
    {
        Uri hubUri = new(_factory.Server.BaseAddress,
            $"/websocket?{ClientConnectionQuery.ClientId}={clientId}&{ClientConnectionQuery.Secret}={SharedSecret}&{ClientConnectionQuery.ClientVersion}=1.0.41");

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        register(connection);

        await connection.StartAsync();

        return connection;
    }

    private async Task<HttpResponseMessage> GetFeedAsync(Guid clientId, string? secret)
    {
        HttpClient http = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/" + ClientUpdateRoutes.Feed(clientId));

        if (secret is not null)
        {
            request.Headers.Add(ClientConnectionQuery.SecretHeader, secret);
        }

        return await http.SendAsync(request);
    }

    [TestMethod]
    public async Task Feed_RejectsRequestsWithoutTheSharedSecret()
    {
        HttpResponseMessage response = await GetFeedAsync(Guid.NewGuid(), secret: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Feed_IsEmpty_ForAKioskTheServerDoesNotKnow()
    {
        HttpResponseMessage response = await GetFeedAsync(Guid.NewGuid(), SharedSecret);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Result<ClientUpdateFeedResponse>? result = await response.Content.ReadFromJsonAsync<Result<ClientUpdateFeedResponse>>(JsonSerializerOptions.Web);
        Assert.IsTrue(result!.IsSuccess);
        Assert.IsNull(result.Data!.TargetVersion);
    }

    [TestMethod]
    public async Task Feed_GivesARegisteredKioskItsStableTarget()
    {
        Guid clientId = Guid.NewGuid();
        await using HubConnection kiosk = await ConnectKioskAsync(clientId, _ => { });

        // Registration happens in the hub's OnConnectedAsync, which can finish just after StartAsync.
        Result<ClientUpdateFeedResponse>? result = null;

        for (int attempt = 0; attempt < 50 && result?.Data?.TargetVersion is null; attempt++)
        {
            HttpResponseMessage response = await GetFeedAsync(clientId, SharedSecret);
            result = await response.Content.ReadFromJsonAsync<Result<ClientUpdateFeedResponse>>(JsonSerializerOptions.Web);
            await Task.Delay(100);
        }

        Assert.AreEqual("1.0.41", result?.Data?.TargetVersion);
        Assert.AreEqual("https://github.example/1.0.41/LanyardClient-1.0.41-full.nupkg", result!.Data!.DownloadUrls["LanyardClient-1.0.41-full.nupkg"]);
    }

    [TestMethod]
    public async Task UpdateNow_ReachesTheKiosksCheckForUpdatesHandler()
    {
        Guid clientId = Guid.NewGuid();
        TaskCompletionSource called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using HubConnection kiosk = await ConnectKioskAsync(clientId, connection =>
            connection.On<bool>(ClientHubEvents.CheckForUpdates, () =>
            {
                called.TrySetResult();
                return Task.FromResult(true);
            }));

        Result<bool> result = Result<bool>.Fail("not called");

        // Wait for OnConnectedAsync to record the kiosk's connection before asking it.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

            result = await clientService.RequestUpdateCheckAsync(clientId);

            if (result.IsSuccess)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data, "The kiosk's answer should come back to the server.");
        Assert.IsTrue(called.Task.IsCompleted);
    }

    [TestMethod]
    public async Task UpdateNow_ReportsAnOldKioskClearly()
    {
        Guid clientId = Guid.NewGuid();

        // No CheckForUpdates handler: a kiosk built before this feature.
        await using HubConnection kiosk = await ConnectKioskAsync(clientId, _ => { });

        Result<bool> result = Result<bool>.Fail("not called");

        for (int attempt = 0; attempt < 50; attempt++)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

            result = await clientService.RequestUpdateCheckAsync(clientId);

            if (result.Error?.Contains("not currently connected") != true && result.Error != "Failed to get client.")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "can't check for updates on request");
    }
}
