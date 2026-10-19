using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The enrolment endpoints through the real pipeline, ending with the kiosk connecting to the hub
// on the credential it was given. The Admin's approval itself is a Blazor dialog, so here it's
// stood in for by marking the request approved, the way KioskEnrolmentService.ApproveAsync does.
[TestClass]
public class KioskEnrolmentIntegrationTests
{
    private const string SharedSecret = "integration-test-kiosk-secret";

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<KioskEnrolmentStartedDTO> StartAsync(Guid clientId)
    {
        HttpResponseMessage response = await _factory.CreateClient()
            .PostAsJsonAsync("/" + KioskEnrolmentRoutes.Requests, new KioskEnrolmentRequestDTO(clientId, "BAR-PC", "1.0.43"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Result<KioskEnrolmentStartedDTO>? result = await response.Content.ReadFromJsonAsync<Result<KioskEnrolmentStartedDTO>>(JsonSerializerOptions.Web);

        return result!.Data!;
    }

    private async Task<HttpResponseMessage> PollAsync(Guid requestId, string? token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/" + KioskEnrolmentRoutes.Request(requestId));

        if (token is not null)
        {
            request.Headers.Add(KioskEnrolmentRoutes.EnrolmentTokenHeader, token);
        }

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task ApproveAsAdminWouldAsync(Guid requestId, int companyId)
    {
        await using ApplicationDbContext ctx = await _factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        ClientEnrolmentRequest row = await ctx.ClientEnrolmentRequests.SingleAsync(x => x.Id == requestId);
        ctx.Clients.Add(new Client { Id = row.ClientId, CompanyId = companyId, Name = "Paired kiosk", CreateDate = DateTime.UtcNow });
        row.Status = ClientEnrolmentStatus.Approved;
        row.ApprovedCompanyId = companyId;
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task NewKiosk_PairsAndConnectsWithItsOwnCredential()
    {
        Guid clientId = Guid.NewGuid();
        KioskEnrolmentStartedDTO started = await StartAsync(clientId);

        HttpResponseMessage pending = await PollAsync(started.RequestId, started.PollToken);
        Assert.AreEqual(KioskEnrolmentState.Pending, (await pending.Content.ReadFromJsonAsync<Result<KioskEnrolmentStatusDTO>>(JsonSerializerOptions.Web))!.Data!.State);

        await ApproveAsAdminWouldAsync(started.RequestId, await KioskTestSupport.GetSeedCompanyIdAsync(_factory));

        HttpResponseMessage approved = await PollAsync(started.RequestId, started.PollToken);
        KioskEnrolmentStatusDTO status = (await approved.Content.ReadFromJsonAsync<Result<KioskEnrolmentStatusDTO>>(JsonSerializerOptions.Web))!.Data!;

        Assert.AreEqual(KioskEnrolmentState.Approved, status.State);
        Assert.AreEqual("no-store", approved.Headers.CacheControl?.ToString());

        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, status.Credential);
        await connection.StartAsync();

        Client? client = await KioskTestSupport.WaitForClientAsync(_factory, clientId, x => x.MostRecentConnectionId != null);
        Assert.AreEqual(ClientAuthMethod.Credential, client?.LastAuthMethod);
    }

    [TestMethod]
    public async Task Poll_WithoutTheTokenIsNotFound()
    {
        KioskEnrolmentStartedDTO started = await StartAsync(Guid.NewGuid());

        Assert.AreEqual(HttpStatusCode.NotFound, (await PollAsync(started.RequestId, null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PollAsync(started.RequestId, "someone-elses-token")).StatusCode);
    }

    [TestMethod]
    public async Task LegacyExchange_GivesAKnownKioskACredential_Once()
    {
        Guid clientId = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskAsync(_factory, clientId);

        HttpResponseMessage first = await ExchangeAsync(clientId, SharedSecret);
        HttpResponseMessage second = await ExchangeAsync(clientId, SharedSecret);

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode, "The shared secret got a second credential for the same kiosk.");

        KioskCredentialIssuedDTO issued = (await first.Content.ReadFromJsonAsync<Result<KioskCredentialIssuedDTO>>(JsonSerializerOptions.Web))!.Data!;

        await using HubConnection connection = KioskTestSupport.CreateCredentialConnection(_factory, issued.Credential);
        await connection.StartAsync();
        Assert.IsNotNull(await KioskTestSupport.WaitForClientAsync(_factory, clientId, x => x.LastAuthMethod == ClientAuthMethod.Credential));
    }

    [TestMethod]
    public async Task LegacyExchange_RefusesAnUnknownKiosk_AndAWrongSecret()
    {
        Assert.AreEqual(HttpStatusCode.BadRequest, (await ExchangeAsync(Guid.NewGuid(), SharedSecret)).StatusCode);

        Guid known = Guid.NewGuid();
        await KioskTestSupport.RegisterKioskAsync(_factory, known);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await ExchangeAsync(known, "wrong-secret")).StatusCode);
    }

    [TestMethod]
    public async Task LegacyExchange_RefusesAKioskCredential()
    {
        Guid clientId = Guid.NewGuid();
        string token = await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, clientId);

        using HttpRequestMessage request = new(HttpMethod.Post, "/" + KioskEnrolmentRoutes.LegacyExchange);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    private async Task<HttpResponseMessage> ExchangeAsync(Guid clientId, string secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/{KioskEnrolmentRoutes.LegacyExchange}?{ClientConnectionQuery.ClientId}={clientId}");
        request.Headers.Add(ClientConnectionQuery.SecretHeader, secret);

        return await _factory.CreateClient().SendAsync(request);
    }
}
