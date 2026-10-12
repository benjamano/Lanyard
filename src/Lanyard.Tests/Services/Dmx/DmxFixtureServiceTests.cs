using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxFixtureServiceTests
{
    private const string TestUserId = "test-user";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // Seeds the built-in fixture definitions (HasData).
        using ApplicationDbContext context = new(options);
        context.Database.EnsureCreated();

        return options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options, int? companyId)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => companyId is null ? new ApplicationDbContext(options) : new ApplicationDbContext(options, new FixedTenantProvider(companyId)));

        return factoryMock;
    }

    private static DmxFixtureService BuildService(DbContextOptions<ApplicationDbContext> options, int? companyId = null)
    {
        Mock<ISecurityService> securityServiceMock = new();
        securityServiceMock.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(TestUserId));

        return new DmxFixtureService(GetFactoryMock(options, companyId).Object, NullLogger<DmxFixtureService>.Instance, securityServiceMock.Object);
    }

    private static DmxFixture NewFixture(Guid clientId, string name, int startChannel, Guid modeId, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Name = name,
        StartChannel = startChannel,
        FixtureModeId = modeId,
        IsActive = isActive,
        CreateByUserId = TestUserId
    };

    private static async Task SeedAsync(DbContextOptions<ApplicationDbContext> options, params DmxFixture[] fixtures)
    {
        await using ApplicationDbContext context = new(options);
        context.DmxFixtures.AddRange(fixtures);
        await context.SaveChangesAsync();
    }

    // A one-mode, company-owned 8-channel definition, as an OFL import or custom fixture would be.
    private static async Task<Guid> SeedCompanyModeAsync(DbContextOptions<ApplicationDbContext> options, int companyId, int channelCount = 8)
    {
        Guid definitionId = Guid.NewGuid();
        Guid modeId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);
        context.DmxFixtureDefinitions.Add(new DmxFixtureDefinition
        {
            Id = definitionId,
            CompanyId = companyId,
            Source = DmxFixtureDefinitionSource.Custom,
            Manufacturer = "Acme",
            Model = "Wash",
            IsActive = true,
            Modes =
            [
                new DmxFixtureMode
                {
                    Id = modeId,
                    DefinitionId = definitionId,
                    Name = $"{channelCount}-channel",
                    ChannelCount = channelCount,
                    Channels = Enumerable.Range(0, channelCount)
                        .Select(offset => new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = modeId, Offset = offset, Label = $"Ch {offset + 1}", Role = DmxChannelRole.Generic })
                        .ToList()
                }
            ]
        });
        await context.SaveChangesAsync();

        return modeId;
    }

    [TestMethod]
    public async Task CreateFixtureAsync_ValidInput_PersistsWithModeAndLegacyType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureService service = BuildService(options);
        Guid clientId = Guid.NewGuid();

        Result<DmxFixture> result = await service.CreateFixtureAsync(clientId, "  Par Can 1 ", 10, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("Par Can 1", result.Data.Name);
        Assert.AreEqual(10, result.Data.StartChannel);
        Assert.AreEqual(BuiltInDmxFixtureDefinitions.RgbModeId, result.Data.FixtureModeId);
        Assert.IsTrue(result.Data.IsActive);

        await using ApplicationDbContext context = new(options);
        DmxFixture saved = await context.DmxFixtures.SingleAsync(f => f.ClientId == clientId);
#pragma warning disable CS0618
        Assert.AreEqual(DmxFixtureType.Rgb, saved.FixtureType, "Built-in modes keep writing the old type so an app rollback still works.");
#pragma warning restore CS0618
    }

    [TestMethod]
    public async Task CreateFixtureAsync_NonBuiltInMode_LeavesLegacyTypeNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid modeId = await SeedCompanyModeAsync(options, companyId: 1);
        DmxFixtureService service = BuildService(options, companyId: 1);

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Wash 1", 1, modeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
#pragma warning disable CS0618
        Assert.IsNull(result.Data!.FixtureType);
#pragma warning restore CS0618
    }

    [TestMethod]
    public async Task CreateFixtureAsync_EmptyName_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "  ", 1, BuiltInDmxFixtureDefinitions.DimmerModeId, allowOverlap: false);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_StartChannelOutOfRange_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Fixture", 513, BuiltInDmxFixtureDefinitions.DimmerModeId, allowOverlap: false);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_FootprintRunsPast512_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        // RGB at 511 would need 511-513.
        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Par", 511, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: true);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "511-513");
    }

    [TestMethod]
    public async Task CreateFixtureAsync_FootprintEndingOn512_Succeeds()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Par", 510, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_UnknownMode_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Par", 1, Guid.NewGuid(), allowOverlap: false);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_ModeFromAnotherCompany_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid otherCompanyModeId = await SeedCompanyModeAsync(options, companyId: 2);
        DmxFixtureService service = BuildService(options, companyId: 1);

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Wash", 1, otherCompanyModeId, allowOverlap: false);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_OverlapWithoutPermission_FailsNamingTheOtherFixture()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        await SeedAsync(options, NewFixture(clientId, "Existing par", 1, BuiltInDmxFixtureDefinitions.RgbModeId));
        DmxFixtureService service = BuildService(options);

        Result<DmxFixture> result = await service.CreateFixtureAsync(clientId, "New par", 3, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Existing par");
    }

    [TestMethod]
    public async Task CreateFixtureAsync_OverlapWithPermission_Succeeds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        await SeedAsync(options, NewFixture(clientId, "Existing par", 1, BuiltInDmxFixtureDefinitions.RgbModeId));
        DmxFixtureService service = BuildService(options);

        Result<DmxFixture> result = await service.CreateFixtureAsync(clientId, "Double patch", 1, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_AdjacentAndOtherClientAndInactive_AreNotOverlaps()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        await SeedAsync(options,
            NewFixture(clientId, "Before", 1, BuiltInDmxFixtureDefinitions.RgbModeId),
            NewFixture(clientId, "Deleted", 4, BuiltInDmxFixtureDefinitions.RgbModeId, isActive: false),
            NewFixture(Guid.NewGuid(), "Other client", 4, BuiltInDmxFixtureDefinitions.RgbModeId));
        DmxFixtureService service = BuildService(options);

        Result<DmxFixture> result = await service.CreateFixtureAsync(clientId, "Next", 4, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task GetOverlapsAsync_ReturnsSharedChannelRange()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        DmxFixture existing = NewFixture(clientId, "Wide", 10, BuiltInDmxFixtureDefinitions.RgbDimmerModeId);
        await SeedAsync(options, existing);
        DmxFixtureService service = BuildService(options);

        Result<IReadOnlyList<DmxFixtureOverlap>> result = await service.GetOverlapsAsync(clientId, 12, 3, null);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual(existing.Id, result.Data[0].FixtureId);
        Assert.AreEqual(12, result.Data[0].FirstSharedChannel);
        Assert.AreEqual(13, result.Data[0].LastSharedChannel);
    }

    [TestMethod]
    public async Task GetFixturesForClientAsync_ExcludesInactive_AndLoadsModeChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        await SeedAsync(options,
            NewFixture(clientId, "Active", 1, BuiltInDmxFixtureDefinitions.RgbModeId),
            NewFixture(clientId, "Inactive", 4, BuiltInDmxFixtureDefinitions.DimmerModeId, isActive: false));
        DmxFixtureService service = BuildService(options);

        Result<IEnumerable<DmxFixture>> result = await service.GetFixturesForClientAsync(clientId);

        Assert.IsTrue(result.IsSuccess);
        List<DmxFixture> fixtures = result.Data!.ToList();
        Assert.AreEqual(1, fixtures.Count);
        Assert.AreEqual("Active", fixtures[0].Name);
        Assert.IsNotNull(fixtures[0].FixtureMode);
        Assert.AreEqual(3, fixtures[0].FixtureMode!.Channels.Count);
    }

    [TestMethod]
    public async Task DeleteFixtureAsync_SetsIsActiveFalse_RatherThanHardDeleting()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixture fixture = NewFixture(Guid.NewGuid(), "Fixture", 1, BuiltInDmxFixtureDefinitions.DimmerModeId);
        await SeedAsync(options, fixture);
        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.DeleteFixtureAsync(fixture.Id);

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext verifyContext = new(options);
        DmxFixture? saved = await verifyContext.DmxFixtures.FirstOrDefaultAsync(f => f.Id == fixture.Id);
        Assert.IsNotNull(saved);
        Assert.IsFalse(saved.IsActive);
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_UpdatesNameStartChannelAndMode()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixture fixture = NewFixture(Guid.NewGuid(), "Old Name", 1, BuiltInDmxFixtureDefinitions.DimmerModeId);
        await SeedAsync(options, fixture);
        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.UpdateFixtureAsync(fixture.Id, "New Name", 20, BuiltInDmxFixtureDefinitions.RgbDimmerModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verifyContext = new(options);
        DmxFixture updated = await verifyContext.DmxFixtures.FirstAsync(f => f.Id == fixture.Id);
        Assert.AreEqual("New Name", updated.Name);
        Assert.AreEqual(20, updated.StartChannel);
        Assert.AreEqual(BuiltInDmxFixtureDefinitions.RgbDimmerModeId, updated.FixtureModeId);
#pragma warning disable CS0618
        Assert.AreEqual(DmxFixtureType.RgbDimmer, updated.FixtureType);
#pragma warning restore CS0618
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_RenamingAFixtureThatAlreadyOverlaps_Succeeds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        DmxFixture first = NewFixture(clientId, "First", 1, BuiltInDmxFixtureDefinitions.RgbModeId);
        DmxFixture second = NewFixture(clientId, "Second", 2, BuiltInDmxFixtureDefinitions.RgbModeId);
        await SeedAsync(options, first, second);
        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.UpdateFixtureAsync(second.Id, "Renamed", 2, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_RenamingAFixtureThatAlreadyRunsPast512_Succeeds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixture legacy = NewFixture(Guid.NewGuid(), "Legacy", 511, BuiltInDmxFixtureDefinitions.RgbModeId);
        await SeedAsync(options, legacy);
        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.UpdateFixtureAsync(legacy.Id, "Renamed", 511, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_MovingOntoAnotherFixture_FailsWithoutPermission()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        DmxFixture first = NewFixture(clientId, "First", 1, BuiltInDmxFixtureDefinitions.RgbModeId);
        DmxFixture second = NewFixture(clientId, "Second", 10, BuiltInDmxFixtureDefinitions.RgbModeId);
        await SeedAsync(options, first, second);
        DmxFixtureService service = BuildService(options);

        Result<bool> blocked = await service.UpdateFixtureAsync(second.Id, "Second", 2, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: false);
        Result<bool> allowed = await service.UpdateFixtureAsync(second.Id, "Second", 2, BuiltInDmxFixtureDefinitions.RgbModeId, allowOverlap: true);

        Assert.IsFalse(blocked.IsSuccess);
        Assert.IsTrue(allowed.IsSuccess, allowed.Error);
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_ChangingToAWiderModeThatRunsPast512_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixture fixture = NewFixture(Guid.NewGuid(), "Dimmer", 510, BuiltInDmxFixtureDefinitions.DimmerModeId);
        await SeedAsync(options, fixture);
        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.UpdateFixtureAsync(fixture.Id, "Dimmer", 510, BuiltInDmxFixtureDefinitions.RgbDimmerModeId, allowOverlap: true);

        Assert.IsFalse(result.IsSuccess);
    }
}
