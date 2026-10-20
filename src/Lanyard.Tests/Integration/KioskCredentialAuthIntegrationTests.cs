using System.Net;
using System.Net.Http.Headers;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Per-kiosk credentials through the real pipeline: the Kiosk authentication scheme, the hub's
// endpoint policy and OnConnectedAsync, the kiosk REST endpoints and the tenant filter. Most of
// these are the "must not get in" cases - the point of the feature.
[TestClass]
public class KioskCredentialAuthIntegrationTests
{
    private const string SharedSecret = "integration-test-kiosk-secret";

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private readonly List<string> _tempFiles = [];

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        // Configured anyway, to prove the old shared secret is no longer accepted even when set.
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();

        foreach (string file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    private HubConnection CreateLegacyConnection(Guid clientId) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, $"/websocket?{ClientConnectionQuery.ClientId}={clientId}&{ClientConnectionQuery.Secret}={SharedSecret}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

    private async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null, string? legacySecret = null)
    {
        HttpClient http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpRequestMessage request = new(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (legacySecret is not null)
        {
            request.Headers.Add(ClientConnectionQuery.SecretHeader, legacySecret);
        }

        return await http.SendAsync(request);
    }

    private static async Task AssertConnectRefusedAsync(HubConnection connection, HttpStatusCode expected)
    {
        HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.AreEqual(expected, ex.StatusCode);
    }

    private async Task<Guid> AddSongAsync(int companyId)
    {
        string path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        _tempFiles.Add(path);

        await using ApplicationDbContext ctx = await _factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        Song song = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Song", AlbumName = "Album", FilePath = path, IsActive = true, IsDownloaded = true, CreateDate = DateTime.UtcNow };
        ctx.Songs.Add(song);
        await ctx.SaveChangesAsync();

        return song.Id;
    }

    // --- Hub ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task Hub_RefusesAConnectionWithNoCredential()
    {
        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, token: null);

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Hub_RefusesAMadeUpCredential()
    {
        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, $"lk1.{Guid.NewGuid():N}.not-the-secret");

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Hub_RefusesARealCredentialIdWithTheWrongSecret()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);
        string forged = token[..token.LastIndexOf('.')] + ".forged-secret";

        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, forged);

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Hub_RefusesARevokedCredential()
    {
        Guid clientId = Guid.NewGuid();
        string oldToken = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        // Issuing again revokes the first one.
        await _factory.Services.GetRequiredService<IKioskCredentialService>().IssueAsync(clientId, ClientCredentialSource.Pairing, null);

        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, oldToken);

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Hub_AcceptsAKioskCredential_AndRecordsTheAuthMethod()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, token);
        await connection.StartAsync();

        Client? client = await KioskTestSupport.WaitForClientAsync(_factory, clientId, x => x.MostRecentConnectionId != null);

        Assert.IsNotNull(client, "The kiosk never showed as connected.");
        Assert.AreEqual(ClientAuthMethod.Credential, client.LastAuthMethod);
    }

    [TestMethod]
    public async Task Hub_UsesTheCredentialsKiosk_NotTheIdInTheUrl()
    {
        Guid kioskA = Guid.NewGuid();
        Guid kioskB = Guid.NewGuid();
        string tokenA = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, kioskA);
        await KioskTestSupport.RegisterKioskAsync(_factory, kioskB);

        // Kiosk A claims to be kiosk B. It must not take over B's connection.
        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, tokenA, claimedClientId: kioskB);
        await connection.StartAsync();

        Assert.IsNotNull(await KioskTestSupport.WaitForClientAsync(_factory, kioskA, x => x.MostRecentConnectionId != null));

        Client? b = await KioskTestSupport.WaitForClientAsync(_factory, kioskB, _ => true);
        Assert.IsNull(b!.MostRecentConnectionId, "Kiosk B's connection was hijacked through the query string.");
    }

    [TestMethod]
    public async Task Hub_NoLongerCreatesAClientForAnUnknownId()
    {
        Guid unknown = Guid.NewGuid();

        await using HubConnection connection = CreateLegacyConnection(unknown);

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);

        await using ApplicationDbContext ctx = await _factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();
        Assert.IsFalse(await ctx.Clients.AnyAsync(x => x.Id == unknown), "The hub created a Client row for an unpaired kiosk.");
    }

    [TestMethod]
    public async Task Hub_RefusesTheSharedSecret_EvenForAKnownKiosk()
    {
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskAsync(_factory, clientId);

        await using HubConnection connection = CreateLegacyConnection(clientId);

        await AssertConnectRefusedAsync(connection, HttpStatusCode.Unauthorized);
    }


    [TestMethod]
    public async Task Hub_IssuesAMediaToken_ToACredentialedKiosk()
    {
        Guid credentialed = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, credentialed);

        await using HubConnection withCredential = KioskTestSupport.CreateCredentialConnection(_factory, token);
        await withCredential.StartAsync();

        KioskMediaTokenDTO? issued = await withCredential.InvokeAsync<KioskMediaTokenDTO?>(ClientHubMethods.IssueMediaToken);

        Assert.IsFalse(string.IsNullOrEmpty(issued?.Token));
    }

    // --- REST --------------------------------------------------------------------------------

    [TestMethod]
    public async Task Feed_ReturnsAKiosksOwnFeed()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        HttpResponseMessage response = await GetAsync("/" + ClientUpdateRoutes.Feed(clientId), bearer: token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Feed_And_OfflineManifest_HideAnotherKiosksData()
    {
        Guid kioskA = Guid.NewGuid();
        Guid kioskB = Guid.NewGuid();
        string tokenA = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, kioskA);
        await KioskTestSupport.RegisterKioskAsync(_factory, kioskB);

        HttpResponseMessage feed = await GetAsync("/" + ClientUpdateRoutes.Feed(kioskB), bearer: tokenA);
        HttpResponseMessage manifest = await GetAsync("/" + KioskOfflineRoutes.Manifest(kioskB), bearer: tokenA);

        Assert.AreEqual(HttpStatusCode.NotFound, feed.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, manifest.StatusCode);
    }

    [TestMethod]
    public async Task Rest_RejectsARevokedCredential_AndTellsTheKioskWhy()
    {
        Guid clientId = Guid.NewGuid();
        string oldToken = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);
        await _factory.Services.GetRequiredService<IKioskCredentialService>().IssueAsync(clientId, ClientCredentialSource.Pairing, null);

        HttpResponseMessage response = await GetAsync("/" + ClientUpdateRoutes.Feed(clientId), bearer: oldToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(response.Headers.WwwAuthenticate.ToString(), "invalid_token");
    }

    [TestMethod]
    public async Task Rest_RejectsTheSharedSecret_RightOrWrong()
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await GetAsync("/api/files/list", legacySecret: "wrong")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await GetAsync("/api/files/list", legacySecret: SharedSecret)).StatusCode);
    }

    [TestMethod]
    public async Task Rest_KioskCredentialCannotReachAStaffOnlyEndpoint()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        HttpClient http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(HttpMethod.Delete, $"/api/files/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await http.SendAsync(request);

        Assert.IsFalse(response.IsSuccessStatusCode, $"A kiosk reached a staff-only endpoint ({response.StatusCode}).");
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode, "The request should be stopped by authorization, not reach the action.");
    }

    [TestMethod]
    public async Task Song_IsOnlyServedToAKioskOfTheSameCompany()
    {
        int companyA = await KioskTestSupport.GetSeedCompanyIdAsync(_factory);
        int companyB = await KioskTestSupport.AddCompanyAsync(_factory, "Other Company");
        Guid songId = await AddSongAsync(companyA);

        string tokenA = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, Guid.NewGuid(), companyA);
        string tokenB = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, Guid.NewGuid(), companyB);

        HttpResponseMessage own = await GetAsync($"/api/music/audio/{songId}", bearer: tokenA);
        HttpResponseMessage other = await GetAsync($"/api/music/audio/{songId}", bearer: tokenB);

        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode, "A kiosk streamed another company's song.");
    }

    [TestMethod]
    public async Task MediaToken_PlaysASong_ButOpensNothingElse()
    {
        int company = await KioskTestSupport.GetSeedCompanyIdAsync(_factory);
        Guid songId = await AddSongAsync(company);
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId, company);

        string mediaToken = await IssueMediaTokenForAsync(clientId);
        string escaped = Uri.EscapeDataString(mediaToken);

        HttpResponseMessage song = await GetAsync($"/api/music/audio/{songId}?{KioskCredentialQuery.MediaToken}={escaped}");
        HttpResponseMessage files = await GetAsync($"/api/files/list?{KioskCredentialQuery.MediaToken}={escaped}");
        HttpResponseMessage feed = await GetAsync($"/{ClientUpdateRoutes.Feed(clientId)}?{KioskCredentialQuery.MediaToken}={escaped}");

        Assert.AreEqual(HttpStatusCode.OK, song.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, files.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, feed.StatusCode);
    }

    [TestMethod]
    public async Task MediaToken_DiesWithItsCredential()
    {
        int company = await KioskTestSupport.GetSeedCompanyIdAsync(_factory);
        Guid songId = await AddSongAsync(company);
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId, company);

        string mediaToken = await IssueMediaTokenForAsync(clientId);

        await _factory.Services.GetRequiredService<IKioskCredentialService>().IssueAsync(clientId, ClientCredentialSource.Pairing, null);

        HttpResponseMessage response = await GetAsync($"/api/music/audio/{songId}?{KioskCredentialQuery.MediaToken}={Uri.EscapeDataString(mediaToken)}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task MediaToken_RejectsATamperedToken()
    {
        int company = await KioskTestSupport.GetSeedCompanyIdAsync(_factory);
        Guid songId = await AddSongAsync(company);

        HttpResponseMessage response = await GetAsync($"/api/music/audio/{songId}?{KioskCredentialQuery.MediaToken}=CfDJ8tampered");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> IssueMediaTokenForAsync(Guid clientId)
    {
        await using ApplicationDbContext ctx = await _factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        ClientCredential credential = await ctx.ClientCredentials.AsNoTracking().SingleAsync(x => x.ClientId == clientId && x.IsActive);
        int companyId = await ctx.Clients.Where(x => x.Id == clientId).Select(x => x.CompanyId).SingleAsync();

        return _factory.Services.GetRequiredService<IKioskMediaTokenService>().Issue(new KioskIdentity(clientId, companyId, credential.Id));
    }
}
