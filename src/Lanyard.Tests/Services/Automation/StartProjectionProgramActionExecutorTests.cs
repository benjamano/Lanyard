using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class StartProjectionProgramActionExecutorTests
{
    private sealed class TestableStartProjectionProgramActionExecutor(
        IServiceScopeFactory scopeFactory,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        ILogger<StartProjectionProgramActionExecutor> logger,
        bool isClientConnected) : StartProjectionProgramActionExecutor(scopeFactory, contextFactory, logger)
    {
        protected override bool IsClientConnected(string connectionId) => isClientConnected;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    private static Mock<IServiceScopeFactory> GetScopeFactoryMock(IProjectionProgramService projectionProgramService)
    {
        Mock<IServiceProvider> providerMock = new();
        providerMock.Setup(p => p.GetService(typeof(IProjectionProgramService)))
            .Returns(projectionProgramService);

        Mock<IServiceScope> scopeMock = new();
        scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

        Mock<IServiceScopeFactory> scopeFactoryMock = new();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        return scopeFactoryMock;
    }

    private static StartProjectionProgramActionExecutor GetExecutor(
        DbContextOptions<ApplicationDbContext> options,
        IProjectionProgramService projectionProgramService,
        bool isClientConnected)
    {
        return new TestableStartProjectionProgramActionExecutor(
            GetScopeFactoryMock(projectionProgramService).Object,
            GetFactoryMock(options).Object,
            new Mock<ILogger<StartProjectionProgramActionExecutor>>().Object,
            isClientConnected);
    }

    private static async Task<Client> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, string? connectionId = "connection-1")
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Client",
            MostRecentConnectionId = connectionId,
            CreateDate = DateTime.UtcNow
        };

        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client;
    }

    private static AutomationRuleAction GetAction(string parametersJson)
    {
        return new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            ActionType = AutomationActionTypes.StartProjectionProgram,
            ParametersJson = parametersJson,
            SortOrder = 0,
            IsActive = true
        };
    }

    [TestMethod]
    public void CanHandle_ShouldReturnTrue_ForStartProjectionProgram()
    {
        StartProjectionProgramActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), new Mock<IProjectionProgramService>().Object, isClientConnected: true);

        Assert.IsTrue(executor.CanHandle(AutomationActionTypes.StartProjectionProgram));
        Assert.IsFalse(executor.CanHandle(AutomationActionTypes.MusicControl));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenParametersInvalidJson()
    {
        StartProjectionProgramActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), new Mock<IProjectionProgramService>().Object, isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("not json"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        Assert.Contains("Projection trigger failed", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenNotConfigured()
    {
        StartProjectionProgramActionExecutor executor = GetExecutor(
            GetInMemoryOptions(), new Mock<IProjectionProgramService>().Object, isClientConnected: true);

        (bool success, string? error) = await executor.ExecuteAsync(GetAction("{}"), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Action not configured with a client and projection program", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotFoundInDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        StartProjectionProgramActionExecutor executor = GetExecutor(
            options, new Mock<IProjectionProgramService>().Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{Guid.NewGuid()}\",\"ProjectionProgramId\":\"{Guid.NewGuid()}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldFail_WhenClientNotConnected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options, connectionId: "stale-connection");
        StartProjectionProgramActionExecutor executor = GetExecutor(
            options, new Mock<IProjectionProgramService>().Object, isClientConnected: false);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"ProjectionProgramId\":\"{Guid.NewGuid()}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("Client not connected", error);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldTriggerProgram_WithDisplayIndex()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid programId = Guid.NewGuid();

        Mock<IProjectionProgramService> serviceMock = new();
        serviceMock.Setup(s => s.TriggerProjectionProgramAsync(programId, client.Id, 2))
            .ReturnsAsync(Result<bool>.Ok(true));

        StartProjectionProgramActionExecutor executor = GetExecutor(options, serviceMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"ProjectionProgramId\":\"{programId}\",\"DisplayIndex\":2}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        Assert.IsNull(error);
        serviceMock.Verify(s => s.TriggerProjectionProgramAsync(programId, client.Id, 2), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldPassNullDisplayIndex_WhenOmittedFromJson()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid programId = Guid.NewGuid();

        Mock<IProjectionProgramService> serviceMock = new();
        serviceMock.Setup(s => s.TriggerProjectionProgramAsync(programId, client.Id, null))
            .ReturnsAsync(Result<bool>.Ok(true));

        StartProjectionProgramActionExecutor executor = GetExecutor(options, serviceMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"ProjectionProgramId\":\"{programId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsTrue(success, error);
        serviceMock.Verify(s => s.TriggerProjectionProgramAsync(programId, client.Id, null), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnError_WhenServiceReturnsFail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Client client = await SeedClientAsync(options);
        Guid programId = Guid.NewGuid();

        Mock<IProjectionProgramService> serviceMock = new();
        serviceMock.Setup(s => s.TriggerProjectionProgramAsync(programId, client.Id, null))
            .ReturnsAsync(Result<bool>.Fail("boom"));

        StartProjectionProgramActionExecutor executor = GetExecutor(options, serviceMock.Object, isClientConnected: true);

        string parametersJson = $"{{\"TargetClientId\":\"{client.Id}\",\"ProjectionProgramId\":\"{programId}\"}}";

        (bool success, string? error) = await executor.ExecuteAsync(GetAction(parametersJson), Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual("boom", error);
    }
}
