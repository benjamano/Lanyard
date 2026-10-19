using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Fake kiosks for the integration tests. The hub no longer creates a Client row for an id it
// doesn't know, so a test kiosk has to be registered first, as an Admin pairing it would.
internal static class KioskTestSupport
{
    public static async Task<int> GetSeedCompanyIdAsync(WebApplicationFactory<Program> factory)
    {
        await using ApplicationDbContext ctx = await factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        return await ctx.Companies.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    public static async Task<int> AddCompanyAsync(WebApplicationFactory<Program> factory, string name)
    {
        await using ApplicationDbContext ctx = await factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        Company company = new() { Name = name, IsActive = true, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company.Id;
    }

    public static async Task RegisterKioskAsync(WebApplicationFactory<Program> factory, Guid clientId, int? companyId = null, string name = "Test kiosk")
    {
        int company = companyId ?? await GetSeedCompanyIdAsync(factory);

        await using ApplicationDbContext ctx = await factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        if (await ctx.Clients.AnyAsync(x => x.Id == clientId))
        {
            return;
        }

        ctx.Clients.Add(new Client { Id = clientId, CompanyId = company, Name = name, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // Registers the kiosk and gives it its own credential; returns the raw token.
    public static async Task<string> RegisterKioskWithCredentialAsync(WebApplicationFactory<Program> factory, Guid clientId, int? companyId = null)
    {
        await RegisterKioskAsync(factory, clientId, companyId);

        Result<IssuedKioskCredential> issued = await factory.Services.GetRequiredService<IKioskCredentialService>()
            .IssueAsync(clientId, ClientCredentialSource.Pairing, createdByUserId: null);

        Assert.IsTrue(issued.IsSuccess, issued.Error);

        return issued.Data!.RawToken;
    }

    // A hub connection the way a kiosk with its own credential makes one: the credential in the
    // Authorization header, never in the URL. Not started.
    public static HubConnection CreateCredentialConnection(WebApplicationFactory<Program> factory, string? token, Guid? claimedClientId = null)
    {
        string query = claimedClientId is Guid id ? $"?{ClientConnectionQuery.ClientId}={id}&{ClientConnectionQuery.DmxBatch}=1" : $"?{ClientConnectionQuery.DmxBatch}=1";

        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/websocket" + query), options =>
            {
                // TestServer has no real sockets; long polling runs over its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();
    }

    // OnConnectedAsync can still be writing when StartAsync returns.
    public static async Task<Client?> WaitForClientAsync(WebApplicationFactory<Program> factory, Guid clientId, Func<Client, bool> condition)
    {
        ISystemDbContextFactory contexts = factory.Services.GetRequiredService<ISystemDbContextFactory>();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            await using ApplicationDbContext ctx = await contexts.CreateDbContextAsync();

            Client? client = await ctx.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId);

            if (client is not null && condition(client))
            {
                return client;
            }

            await Task.Delay(100);
        }

        return null;
    }
}
