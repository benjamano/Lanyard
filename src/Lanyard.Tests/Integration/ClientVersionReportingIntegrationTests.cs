using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// A real SignalR connection through the real pipeline (the /websocket secret gate, the hub's
// OnConnectedAsync, EF). The query keys are untyped strings on the wire, so this is what proves
// the kiosk's ClientConnectionQuery.ClientVersion actually lands on the Client row - a mismatch
// would otherwise just leave every kiosk on "Unknown" with no error anywhere.
[TestClass]
public class ClientVersionReportingIntegrationTests
{
    private const string SharedSecret = "integration-test-kiosk-secret";

    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));

        // Forces host startup (and so DatabaseSeeder) before the hub is used.
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task<HubConnection> ConnectAsync(Guid clientId, string? clientVersion)
    {
        string versionQuery = clientVersion is null
            ? string.Empty
            : $"&{ClientConnectionQuery.ClientVersion}={Uri.EscapeDataString(clientVersion)}";

        Uri hubUri = new(_factory.Server.BaseAddress,
            $"/websocket?{ClientConnectionQuery.ClientId}={clientId}&{ClientConnectionQuery.Secret}={SharedSecret}&{ClientConnectionQuery.DmxBatch}=1{versionQuery}");

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                // The TestServer has no real socket; long polling runs over its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();

        return connection;
    }

    // OnConnectedAsync can still be finishing its DB write when StartAsync returns.
    private async Task<Client?> WaitForClientAsync(Guid clientId, Func<Client, bool> condition)
    {
        ISystemDbContextFactory factory = _factory.Services.GetRequiredService<ISystemDbContextFactory>();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == clientId);

            if (client is not null && condition(client))
            {
                return client;
            }

            await Task.Delay(100);
        }

        return null;
    }

    [TestMethod]
    public async Task NewKiosk_ReportingVersion_IsStoredOnItsClientRow()
    {
        Guid clientId = Guid.NewGuid();

        await using HubConnection connection = await ConnectAsync(clientId, "1.0.41");

        Client? client = await WaitForClientAsync(clientId, x => x.ReportedClientVersion is not null);

        Assert.IsNotNull(client, "The hub never stored the reported version.");
        Assert.AreEqual("1.0.41", client.ReportedClientVersion);
        Assert.IsNotNull(client.ClientVersionReportedAt);
    }

    [TestMethod]
    public async Task KnownKiosk_ReconnectingOnOldBuild_KeepsLastReportedVersion()
    {
        Guid clientId = Guid.NewGuid();

        await using (HubConnection first = await ConnectAsync(clientId, "1.0.41"))
        {
            Assert.IsNotNull(await WaitForClientAsync(clientId, x => x.ReportedClientVersion == "1.0.41"));
        }

        // Same kiosk, but a build that predates version reporting - it sends no version at all.
        await using HubConnection second = await ConnectAsync(clientId, null);

        Client? client = await WaitForClientAsync(clientId, x => x.MostRecentConnectionId != null && x.LastUpdateDate != null);

        Assert.IsNotNull(client, "The reconnect never updated the client row.");
        Assert.AreEqual("1.0.41", client.ReportedClientVersion);
    }

    [TestMethod]
    public async Task Kiosk_ReportingGarbageVersion_IsNotStored()
    {
        Guid clientId = Guid.NewGuid();

        await using HubConnection connection = await ConnectAsync(clientId, "not-a-version");

        Client? client = await WaitForClientAsync(clientId, _ => true);

        Assert.IsNotNull(client, "The kiosk should still have been registered.");
        Assert.IsNull(client.ReportedClientVersion);
    }
}
