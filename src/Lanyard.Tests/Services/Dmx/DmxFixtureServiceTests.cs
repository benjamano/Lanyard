using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
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

    private static DmxFixtureService BuildService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISecurityService> securityServiceMock = new();
        securityServiceMock.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(TestUserId));

        return new DmxFixtureService(GetFactoryMock(options).Object, NullLogger<DmxFixtureService>.Instance, securityServiceMock.Object);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_ValidInput_PersistsAndReturnsFixture()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureService service = BuildService(options);
        Guid clientId = Guid.NewGuid();

        Result<DmxFixture> result = await service.CreateFixtureAsync(clientId, "Par Can 1", 10, DmxFixtureType.Rgb);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual("Par Can 1", result.Data.Name);
        Assert.AreEqual(10, result.Data.StartChannel);
        Assert.AreEqual(DmxFixtureType.Rgb, result.Data.FixtureType);
        Assert.IsTrue(result.Data.IsActive);

        await using ApplicationDbContext context = new(options);
        Assert.AreEqual(1, await context.DmxFixtures.CountAsync(f => f.ClientId == clientId));
    }

    [TestMethod]
    public async Task CreateFixtureAsync_EmptyName_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "  ", 1, DmxFixtureType.Dimmer);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFixtureAsync_StartChannelOutOfRange_Fails()
    {
        DmxFixtureService service = BuildService(GetInMemoryOptions());

        Result<DmxFixture> result = await service.CreateFixtureAsync(Guid.NewGuid(), "Fixture", 513, DmxFixtureType.Dimmer);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetFixturesForClientAsync_ExcludesInactiveFixtures()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await using (ApplicationDbContext context = new(options))
        {
            context.DmxFixtures.AddRange(
                new DmxFixture { Id = Guid.NewGuid(), ClientId = clientId, Name = "Active", StartChannel = 1, FixtureType = DmxFixtureType.Dimmer, IsActive = true, CreateByUserId = TestUserId },
                new DmxFixture { Id = Guid.NewGuid(), ClientId = clientId, Name = "Inactive", StartChannel = 2, FixtureType = DmxFixtureType.Dimmer, IsActive = false, CreateByUserId = TestUserId });

            await context.SaveChangesAsync();
        }

        DmxFixtureService service = BuildService(options);

        Result<IEnumerable<DmxFixture>> result = await service.GetFixturesForClientAsync(clientId);

        Assert.IsTrue(result.IsSuccess);
        List<DmxFixture> fixtures = result.Data!.ToList();
        Assert.AreEqual(1, fixtures.Count);
        Assert.AreEqual("Active", fixtures[0].Name);
    }

    [TestMethod]
    public async Task DeleteFixtureAsync_SetsIsActiveFalse_RatherThanHardDeleting()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid fixtureId = Guid.NewGuid();

        await using (ApplicationDbContext context = new(options))
        {
            context.DmxFixtures.Add(new DmxFixture { Id = fixtureId, ClientId = clientId, Name = "Fixture", StartChannel = 1, FixtureType = DmxFixtureType.Dimmer, IsActive = true, CreateByUserId = TestUserId });
            await context.SaveChangesAsync();
        }

        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.DeleteFixtureAsync(fixtureId);

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext verifyContext = new(options);
        DmxFixture? fixture = await verifyContext.DmxFixtures.FirstOrDefaultAsync(f => f.Id == fixtureId);
        Assert.IsNotNull(fixture);
        Assert.IsFalse(fixture.IsActive);
    }

    [TestMethod]
    public async Task UpdateFixtureAsync_UpdatesNameStartChannelAndType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid fixtureId = Guid.NewGuid();

        await using (ApplicationDbContext context = new(options))
        {
            context.DmxFixtures.Add(new DmxFixture { Id = fixtureId, ClientId = clientId, Name = "Old Name", StartChannel = 1, FixtureType = DmxFixtureType.Dimmer, IsActive = true, CreateByUserId = TestUserId });
            await context.SaveChangesAsync();
        }

        DmxFixtureService service = BuildService(options);

        Result<bool> result = await service.UpdateFixtureAsync(new DmxFixture
        {
            Id = fixtureId,
            ClientId = clientId,
            Name = "New Name",
            StartChannel = 20,
            FixtureType = DmxFixtureType.RgbDimmer,
            CreateByUserId = TestUserId
        });

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext verifyContext = new(options);
        DmxFixture updated = await verifyContext.DmxFixtures.FirstAsync(f => f.Id == fixtureId);
        Assert.AreEqual("New Name", updated.Name);
        Assert.AreEqual(20, updated.StartChannel);
        Assert.AreEqual(DmxFixtureType.RgbDimmer, updated.FixtureType);
    }
}
