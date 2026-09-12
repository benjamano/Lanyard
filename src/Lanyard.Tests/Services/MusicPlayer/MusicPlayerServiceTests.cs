using Lanyard.Application.Services;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.MusicPlayer;

[TestClass]
public class MusicPlayerServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [TestMethod]
    public async Task Pause_ResolvesConnectionIdViaClientService_NotDirectDatabaseQuery()
    {
        Guid clientId = Guid.NewGuid();
        string connectionId = "connection-1";

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<ISingleClientProxy> proxyMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IHubContext<SignalRControlHub>> hubMock = new();
        clientsMock.Setup(c => c.Client(connectionId)).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        // MusicPlayerService is a singleton, so it can't constructor-inject the scoped
        // IClientService - it resolves it per call via IServiceScopeFactory instead (same
        // pattern DmxService uses). This test asserts that path is actually taken, and that
        // GetClientConnectionIdAsync no longer queries the database directly itself.
        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientCurrentConnectionIdAsync(clientId))
            .ReturnsAsync(Result<string?>.Ok(connectionId));

        Mock<IServiceScope> scopeMock = new();
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientServiceMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factoryMock.Object,
            scopeFactoryMock.Object,
            NullLogger<MusicPlayerService>.Instance);

        await player.Pause(clientId);

        clientServiceMock.Verify(x => x.GetClientCurrentConnectionIdAsync(clientId), Times.Once);
        proxyMock.Verify(
            p => p.SendCoreAsync("Pause", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The DB context factory should never be invoked directly for connection resolution -
        // that's IClientService's job (behind its own cache), reached only via the scope above.
        factoryMock.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Pause_DoesNotSendCommand_WhenClientServiceCannotResolveConnection()
    {
        Guid clientId = Guid.NewGuid();

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IHubContext<SignalRControlHub>> hubMock = new();
        Mock<IHubClients> clientsMock = new();
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        Mock<IClientService> clientServiceMock = new();
        clientServiceMock.Setup(x => x.GetClientCurrentConnectionIdAsync(clientId))
            .ReturnsAsync(Result<string?>.Ok(null));

        Mock<IServiceScope> scopeMock = new();
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(clientServiceMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        MusicPlayerService player = new(
            hubMock.Object,
            factoryMock.Object,
            scopeFactoryMock.Object,
            NullLogger<MusicPlayerService>.Instance);

        await player.Pause(clientId);

        clientsMock.Verify(c => c.Client(It.IsAny<string>()), Times.Never);
    }
}
