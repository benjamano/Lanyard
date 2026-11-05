using Lanyard.Application.Services.Clients;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ClientZoneScoreboardServiceTests
{
    private const string ConnectionId = "connection-1";

    private static (ClientZoneScoreboardService Service, Mock<ISingleClientProxy> Proxy, DbContextOptions<ApplicationDbContext> Options) Build()
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

        ClientZoneScoreboardService service = new(factoryMock.Object, hubContextMock.Object, new Mock<ILogger<ClientZoneScoreboardService>>().Object);

        return (service, proxyMock, options);
    }

    private static async Task<Guid> SeedClientAsync(DbContextOptions<ApplicationDbContext> options, string? connectionId = ConnectionId)
    {
        Client client = new() { Id = Guid.NewGuid(), Name = "Kiosk", MostRecentConnectionId = connectionId };

        await using ApplicationDbContext ctx = new(options);
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static ZoneScoreboardSettings Settings(Guid clientId, bool filter, bool promiscuous) => new()
    {
        ClientId = clientId,
        PreferredDeviceMacAddress = "00-11-22-AA-BB-CC",
        SourceIp = "192.168.0.10",
        DestinationIp = "192.168.0.255",
        ZoneScoreboardVersion = ZoneScoreboardVersion.Begeara2,
        IsActive = true,
        UseCaptureFilter = filter,
        PromiscuousCapture = promiscuous,
    };

    [TestMethod]
    public void NewSettingsAndDto_DefaultToHowKiosksAlwaysCaptured()
    {
        ZoneScoreboardSettings settings = new() { ClientId = Guid.NewGuid(), PreferredDeviceMacAddress = "", SourceIp = "" };
        ZoneScoreboardSettingsDTO dto = new() { PreferredDeviceMacAddress = "", SourceIp = "" };

        Assert.IsFalse(settings.UseCaptureFilter);
        Assert.IsTrue(settings.PromiscuousCapture);
        Assert.IsFalse(dto.UseCaptureFilter);
        Assert.IsTrue(dto.PromiscuousCapture);
    }

    [TestMethod]
    public void OldServerPayload_WithoutTheNewFields_LeavesTheKioskUnchanged()
    {
        ZoneScoreboardSettingsDTO? dto = System.Text.Json.JsonSerializer.Deserialize<ZoneScoreboardSettingsDTO>(
            """{"preferredDeviceMacAddress":"00-11-22-AA-BB-CC","zoneScoreboardVersion":1,"destinationIp":"192.168.0.255","sourceIp":"192.168.0.10"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsFalse(dto!.UseCaptureFilter);
        Assert.IsTrue(dto.PromiscuousCapture);
    }

    [TestMethod]
    public void ToDto_CarriesTheCaptureOptions()
    {
        ZoneScoreboardSettingsDTO dto = ClientZoneScoreboardService.ToDto(Settings(Guid.NewGuid(), filter: true, promiscuous: false));

        Assert.IsTrue(dto.UseCaptureFilter);
        Assert.IsFalse(dto.PromiscuousCapture);
        Assert.AreEqual("192.168.0.10", dto.SourceIp);
        Assert.AreEqual("192.168.0.255", dto.DestinationIp);
        Assert.AreEqual("00-11-22-AA-BB-CC", dto.PreferredDeviceMacAddress);
    }

    [TestMethod]
    public async Task Update_SavesTheCaptureOptions_ForNewAndExistingSettings()
    {
        (ClientZoneScoreboardService service, _, DbContextOptions<ApplicationDbContext> options) = Build();
        Guid clientId = await SeedClientAsync(options);

        Result<bool> created = await service.UpdateZoneScoreboardSettingsAsync(Settings(clientId, filter: true, promiscuous: false));
        Assert.IsTrue(created.IsSuccess, created.Error);

        ZoneScoreboardSettings? saved = (await service.GetZoneScoreboardSettingsAsync(clientId)).Data;
        Assert.IsTrue(saved!.UseCaptureFilter);
        Assert.IsFalse(saved.PromiscuousCapture);

        Result<bool> updated = await service.UpdateZoneScoreboardSettingsAsync(Settings(clientId, filter: false, promiscuous: true));
        Assert.IsTrue(updated.IsSuccess, updated.Error);

        saved = (await service.GetZoneScoreboardSettingsAsync(clientId)).Data;
        Assert.IsFalse(saved!.UseCaptureFilter);
        Assert.IsTrue(saved.PromiscuousCapture);
    }

    [TestMethod]
    public async Task Update_SendsTheNewSettingsToTheConnectedKiosk()
    {
        (ClientZoneScoreboardService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build();
        Guid clientId = await SeedClientAsync(options);

        await service.UpdateZoneScoreboardSettingsAsync(Settings(clientId, filter: true, promiscuous: false));

        proxy.Verify(p => p.SendCoreAsync(ClientHubMethods.ReceiveZoneScoreboardSettings,
            It.Is<object?[]>(args => args.Length == 1
                && ((ZoneScoreboardSettingsDTO)args[0]!).UseCaptureFilter
                && !((ZoneScoreboardSettingsDTO)args[0]!).PromiscuousCapture),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Update_KioskNeverConnected_StillSaves()
    {
        (ClientZoneScoreboardService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build();
        Guid clientId = await SeedClientAsync(options, connectionId: null);

        Result<bool> result = await service.UpdateZoneScoreboardSettingsAsync(Settings(clientId, filter: true, promiscuous: true));

        Assert.IsTrue(result.IsSuccess, result.Error);
        proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Update_SendFails_StillSaves()
    {
        (ClientZoneScoreboardService service, Mock<ISingleClientProxy> proxy, DbContextOptions<ApplicationDbContext> options) = Build();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("gone"));
        Guid clientId = await SeedClientAsync(options);

        Result<bool> result = await service.UpdateZoneScoreboardSettingsAsync(Settings(clientId, filter: true, promiscuous: true));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue((await service.GetZoneScoreboardSettingsAsync(clientId)).Data!.UseCaptureFilter);
    }

    [TestMethod]
    public void ReceiveZoneScoreboardSettings_KeepsTheNameKiosksListenFor()
    {
        // Kiosks in the field register this exact string; never rename it.
        Assert.AreEqual("ReceiveZoneScoreboardSettings", ClientHubMethods.ReceiveZoneScoreboardSettings);
    }
}
