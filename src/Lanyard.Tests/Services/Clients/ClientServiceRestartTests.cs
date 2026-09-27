using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ClientServiceRestartTests
{
    private const string ConnectionId = "connection-1";

    // SignalRControlHub.ConnectedIds is a static set, so connectivity goes through the same
    // overridable seam ClientServiceTests uses.
    private sealed class TestableClientService(
        IDbContextFactory<ApplicationDbContext> factory,
        IHubContext<SignalRControlHub> hubContext,
        IReadOnlyCollection<string> connectedIds)
        : ClientService(factory, hubContext, new Mock<ILogger<ClientService>>().Object, new MemoryCache(new MemoryCacheOptions()))
    {
        protected override IReadOnlyCollection<string> GetConnectedConnectionIds()
        {
            return connectedIds;
        }
    }

    private static (TestableClientService Service, Mock<ISingleClientProxy> Proxy, DbContextOptions<ApplicationDbContext> Options) Build(params string[] connectedIds)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> hubClientsMock = new();
        hubClientsMock.Setup(c => c.Client(ConnectionId)).Returns(proxyMock.Object);

        Mock<IHubContext<SignalRControlHub>> hubContextMock = new();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        return (new TestableClientService(factoryMock.Object, hubContextMock.Object, connectedIds), proxyMock, options);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Client client = new() { Id = Guid.NewGuid(), Name = "Kiosk", MostRecentConnectionId = ConnectionId };

        await using ApplicationDbContext ctx = new(options);
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    [TestMethod]
    [DataRow(ClientRestartType.Application)]
    [DataRow(ClientRestartType.Computer)]
    public async Task RestartClientAsync_SendsRestartCommandToConnectedClient(ClientRestartType restartType)
    {
        (TestableClientService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build(ConnectionId);
        Guid clientId = await SeedClientAsync(options);

        Result<bool> result = await service.RestartClientAsync(clientId, restartType);

        Assert.IsTrue(result.IsSuccess, result.Error);
        proxy.Verify(p => p.SendCoreAsync("RestartClient",
            It.Is<object?[]>(args => args.Length == 1 && (ClientRestartType)args[0]! == restartType),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RestartClientAsync_FailsWithoutSendingWhenClientIsOffline()
    {
        // The client still has a stale MostRecentConnectionId, which is exactly the case that
        // must not be treated as reachable.
        (TestableClientService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build();
        Guid clientId = await SeedClientAsync(options);

        Result<bool> result = await service.RestartClientAsync(clientId, ClientRestartType.Application);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Client is not currently connected.", result.Error);
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RestartClientAsync_FailsWhenClientDoesNotExist()
    {
        (TestableClientService service, Mock<ISingleClientProxy> proxy, _) = Build(ConnectionId);

        Result<bool> result = await service.RestartClientAsync(Guid.NewGuid(), ClientRestartType.Application);

        Assert.IsFalse(result.IsSuccess);
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RestartClientAsync_RejectsUnknownRestartType()
    {
        (TestableClientService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build(ConnectionId);
        Guid clientId = await SeedClientAsync(options);

        Result<bool> result = await service.RestartClientAsync(clientId, (ClientRestartType)99);

        Assert.IsFalse(result.IsSuccess);
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
