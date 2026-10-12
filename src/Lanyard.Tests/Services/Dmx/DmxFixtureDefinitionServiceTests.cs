using System.Text;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Dmx;
using Lanyard.Application.Services.Dmx.Ofl;
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
public class DmxFixtureDefinitionServiceTests
{
    private const string TestUserId = "test-user";

    private const string ParJson = """
    {
      "name": "Flat Pro 18",
      "meta": { "lastModifyDate": "2018-08-09" },
      "availableChannels": {
        "Red": { "capability": { "type": "ColorIntensity", "color": "Red" } },
        "Green": { "capability": { "type": "ColorIntensity", "color": "Green" } },
        "Blue": { "capability": { "type": "ColorIntensity", "color": "Blue" } },
        "Master": { "capability": { "type": "Intensity" } }
      },
      "modes": [
        { "name": "3-channel", "channels": ["Red", "Green", "Blue"] },
        { "name": "4-channel", "channels": ["Master", "Red", "Green", "Blue"] },
        { "name": "Pixel", "channels": ["Master", { "insert": "matrixChannels", "repeatFor": "eachPixelXYZ", "channelOrder": "perPixel", "templateChannels": ["Red $pixelKey"] }] }
      ]
    }
    """;

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using ApplicationDbContext context = new(options);
        context.Database.EnsureCreated();

        return options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options, new FixedTenantProvider(companyId)));

        return factoryMock;
    }

    private static DmxFixtureDefinitionService BuildService(DbContextOptions<ApplicationDbContext> options, int companyId, string fixtureJson = ParJson)
    {
        OflFixtureParser parser = new();

        Mock<IOflCatalogueService> catalogue = new();
        catalogue.Setup(c => c.GetFixture("cameo/flat-pro-18"))
            .Returns(() => parser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(fixtureJson)), "cameo/flat-pro-18", "Cameo"));
        catalogue.Setup(c => c.GetFixture(It.Is<string>(k => k != "cameo/flat-pro-18")))
            .Returns(Result<OflParsedFixture>.Fail("That fixture isn't in the library."));

        Mock<ISecurityService> security = new();
        security.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(TestUserId));

        return new DmxFixtureDefinitionService(GetFactoryMock(options, companyId).Object, catalogue.Object, parser, security.Object, NullLogger<DmxFixtureDefinitionService>.Instance);
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_CopiesOnlySupportedModesIntoTheCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinitionService service = BuildService(options, companyId: 1);

        Result<DmxFixtureDefinition> result = await service.ImportFromLibraryAsync("cameo/flat-pro-18");

        Assert.IsTrue(result.IsSuccess, result.Error);
        DmxFixtureDefinition definition = result.Data!;
        Assert.AreEqual(1, definition.CompanyId);
        Assert.AreEqual(DmxFixtureDefinitionSource.Ofl, definition.Source);
        Assert.AreEqual("Cameo", definition.Manufacturer);
        Assert.AreEqual("2018-08-09", definition.OflRevision);
        CollectionAssert.AreEqual(new[] { "3-channel", "4-channel" }, definition.Modes.OrderBy(m => m.SortOrder).Select(m => m.Name).ToArray());

        await using ApplicationDbContext verify = new(options);
        DmxFixtureMode fourChannel = await verify.DmxFixtureModes.Include(m => m.Channels).SingleAsync(m => m.DefinitionId == definition.Id && m.Name == "4-channel");
        Assert.AreEqual(4, fourChannel.ChannelCount);
        Assert.AreEqual(DmxChannelRole.Intensity, fourChannel.Channels.Single(c => c.Offset == 0).Role);
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_SameFixtureTwice_ReusesTheCompanysCopy()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinitionService service = BuildService(options, companyId: 1);

        Result<DmxFixtureDefinition> first = await service.ImportFromLibraryAsync("cameo/flat-pro-18");
        Result<DmxFixtureDefinition> second = await service.ImportFromLibraryAsync("cameo/flat-pro-18");

        Assert.AreEqual(first.Data!.Id, second.Data!.Id);

        await using ApplicationDbContext verify = new(options);
        Assert.AreEqual(1, await verify.DmxFixtureDefinitions.CountAsync(d => d.Source == DmxFixtureDefinitionSource.Ofl));
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_SameFixtureInTwoCompanies_GivesEachItsOwnCopy()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<DmxFixtureDefinition> forA = await BuildService(options, companyId: 1).ImportFromLibraryAsync("cameo/flat-pro-18");
        Result<DmxFixtureDefinition> forB = await BuildService(options, companyId: 2).ImportFromLibraryAsync("cameo/flat-pro-18");

        Assert.AreNotEqual(forA.Data!.Id, forB.Data!.Id);
        Assert.AreEqual(2, forB.Data.CompanyId);
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_NewerRevision_CreatesANewCopyAndLeavesTheOldOne()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<DmxFixtureDefinition> original = await BuildService(options, 1).ImportFromLibraryAsync("cameo/flat-pro-18");
        Result<DmxFixtureDefinition> updated = await BuildService(options, 1, ParJson.Replace("2018-08-09", "2025-01-01")).ImportFromLibraryAsync("cameo/flat-pro-18");

        Assert.AreNotEqual(original.Data!.Id, updated.Data!.Id);

        await using ApplicationDbContext verify = new(options);
        Assert.IsTrue(await verify.DmxFixtureDefinitions.AnyAsync(d => d.Id == original.Data.Id && d.IsActive));
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_UnknownKey_Fails()
    {
        Result<DmxFixtureDefinition> result = await BuildService(GetInMemoryOptions(), 1).ImportFromLibraryAsync("nobody/nothing");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ImportFromLibraryAsync_OnlyPixelModes_Fails()
    {
        string pixelOnly = """{ "name": "Pixel Bar", "availableChannels": { "M": { "capability": { "type": "Intensity" } } }, "modes": [ { "name": "Pixel", "channels": ["M", { "insert": "matrixChannels", "repeatFor": "eachPixelXYZ", "channelOrder": "perPixel", "templateChannels": ["Red $pixelKey"] }] } ] }""";

        Result<DmxFixtureDefinition> result = await BuildService(GetInMemoryOptions(), 1, pixelOnly).ImportFromLibraryAsync("cameo/flat-pro-18");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Pixel");
    }

    [TestMethod]
    public async Task ImportFromFileAsync_UsesTheGivenManufacturer_AndDeduplicatesByModelAndRevision()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinitionService service = BuildService(options, 1);

        Result<DmxFixtureDefinition> first = await service.ImportFromFileAsync(new MemoryStream(Encoding.UTF8.GetBytes(ParJson)), " Cameo ");
        Result<DmxFixtureDefinition> second = await service.ImportFromFileAsync(new MemoryStream(Encoding.UTF8.GetBytes(ParJson)), "Cameo");

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.AreEqual("Cameo", first.Data!.Manufacturer);
        Assert.IsNull(first.Data.OflKey);
        Assert.AreEqual(first.Data.Id, second.Data!.Id);
    }

    [TestMethod]
    public async Task ImportFromFileAsync_NoManufacturer_Fails()
    {
        Result<DmxFixtureDefinition> result = await BuildService(GetInMemoryOptions(), 1).ImportFromFileAsync(new MemoryStream(Encoding.UTF8.GetBytes(ParJson)), "  ");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ImportFromFileAsync_InvalidFile_Fails()
    {
        Result<DmxFixtureDefinition> result = await BuildService(GetInMemoryOptions(), 1).ImportFromFileAsync(new MemoryStream(Encoding.UTF8.GetBytes("{ nope")), "Cameo");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetDefinitionsAsync_ReturnsBuiltInsAndOnlyThisCompanysImports()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await BuildService(options, companyId: 2).ImportFromLibraryAsync("cameo/flat-pro-18");
        await BuildService(options, companyId: 1).ImportFromFileAsync(new MemoryStream(Encoding.UTF8.GetBytes(ParJson.Replace("Flat Pro 18", "Own Par"))), "Acme");

        Result<IReadOnlyList<DmxFixtureDefinition>> result = await BuildService(options, companyId: 1).GetDefinitionsAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        List<string> models = result.Data!.Select(d => d.Model).ToList();
        CollectionAssert.AreEqual(new[] { "Dimmer", "RGB", "RGB + Dimmer", "Own Par" }, models);
        Assert.IsTrue(result.Data.All(d => d.Modes.All(m => m.Channels.Count == m.ChannelCount)));
    }

    private static CustomFixtureChannelInput Ch(string label, DmxChannelRole role, bool isFine = false, params CustomFixtureColorRangeInput[] ranges) =>
        new(label, role, isFine, ranges);

    private static CustomFixtureInput MovingHead(string model = "Spot 60") => new("Acme", model,
    [
        new CustomFixtureModeInput("Standard",
        [
            Ch("Pan", DmxChannelRole.Pan),
            Ch("Pan fine", DmxChannelRole.Generic, isFine: true),
            Ch("Tilt", DmxChannelRole.Tilt),
            Ch("Colour", DmxChannelRole.ColorWheel, false, new CustomFixtureColorRangeInput(0, 9, "#ffffff"), new CustomFixtureColorRangeInput(10, 19, "#ff0000")),
            Ch("Dimmer", DmxChannelRole.Intensity, false, new CustomFixtureColorRangeInput(0, 10, "#000000")),
        ])
    ]);

    [TestMethod]
    public async Task CreateCustomAsync_SavesModesChannelsFineLinksAndRanges()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Result<DmxFixtureDefinition> result = await BuildService(options, 1).CreateCustomAsync(MovingHead());

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(DmxFixtureDefinitionSource.Custom, result.Data!.Source);
        Assert.AreEqual(1, result.Data.CompanyId);

        await using ApplicationDbContext verify = new(options);
        DmxFixtureMode mode = await verify.DmxFixtureModes
            .Include(m => m.Channels).ThenInclude(c => c.ColorRanges)
            .SingleAsync(m => m.DefinitionId == result.Data.Id);
        List<DmxFixtureModeChannel> channels = mode.Channels.OrderBy(c => c.Offset).ToList();

        Assert.AreEqual(5, mode.ChannelCount);
        Assert.AreEqual(DmxChannelRole.Pan, channels[1].Role, "A fine channel takes its coarse channel's role.");
        Assert.AreEqual(0, channels[1].CoarseOffset);
        Assert.AreEqual((byte)1, channels[1].FineLevel);
        Assert.AreEqual(2, channels[3].ColorRanges.Count);
        Assert.AreEqual("#FF0000", channels[3].ColorRanges.Single(r => r.DmxStart == 10).ColorHex);
        Assert.AreEqual(0, channels[4].ColorRanges.Count, "Ranges are only kept on colour wheel, macro and strobe channels.");
    }

    [TestMethod]
    public async Task CreateCustomAsync_InvalidInput_Fails()
    {
        DmxFixtureDefinitionService service = BuildService(GetInMemoryOptions(), 1);
        CustomFixtureModeInput okMode = new("Standard", [Ch("Dimmer", DmxChannelRole.Intensity)]);

        CustomFixtureInput[] invalid =
        [
            new(" ", "Model", [okMode]),
            new("Acme", "", [okMode]),
            new("Acme", new string('x', 101), [okMode]),
            new("Acme", "Model", []),
            new("Acme", "Model", [okMode, okMode]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [])]),
            new("Acme", "Model", [new CustomFixtureModeInput(" ", [Ch("Dimmer", DmxChannelRole.Intensity)])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [Ch("", DmxChannelRole.Intensity)])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [Ch("Fine first", DmxChannelRole.Pan, isFine: true)])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [Ch("Wheel", DmxChannelRole.ColorWheel, false, new CustomFixtureColorRangeInput(20, 10, "#ffffff"))])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [Ch("Wheel", DmxChannelRole.ColorWheel, false, new CustomFixtureColorRangeInput(0, 10, "red"))])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", [Ch("Odd", (DmxChannelRole)999)])]),
            new("Acme", "Model", [new CustomFixtureModeInput("Standard", Enumerable.Range(0, 513).Select(i => Ch($"C{i}", DmxChannelRole.Generic)).ToList())]),
        ];

        foreach (CustomFixtureInput input in invalid)
        {
            Result<DmxFixtureDefinition> result = await service.CreateCustomAsync(input);
            Assert.IsFalse(result.IsSuccess, $"Expected failure for {input.Manufacturer}/{input.Model}/{input.Modes.Count} modes");
        }
    }

    [TestMethod]
    public async Task DeleteDefinitionAsync_Unused_SoftDeletes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinitionService service = BuildService(options, 1);
        DmxFixtureDefinition created = (await service.CreateCustomAsync(MovingHead())).Data!;

        Result<bool> result = await service.DeleteDefinitionAsync(created.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext verify = new(options);
        Assert.IsFalse((await verify.DmxFixtureDefinitions.SingleAsync(d => d.Id == created.Id)).IsActive);
        Assert.IsFalse((await service.GetDefinitionsAsync()).Data!.Any(d => d.Id == created.Id));
    }

    [TestMethod]
    public async Task DeleteDefinitionAsync_InUse_FailsNamingTheFixture()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinitionService service = BuildService(options, 1);
        DmxFixtureDefinition created = (await service.CreateCustomAsync(MovingHead())).Data!;

        await using (ApplicationDbContext context = new(options))
        {
            context.DmxFixtures.Add(new DmxFixture { Id = Guid.NewGuid(), CompanyId = 1, ClientId = Guid.NewGuid(), Name = "Stage spot", StartChannel = 1, FixtureModeId = created.Modes.Single().Id, IsActive = true, CreateByUserId = TestUserId });
            await context.SaveChangesAsync();
        }

        Result<bool> result = await service.DeleteDefinitionAsync(created.Id);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Stage spot");
    }

    [TestMethod]
    public async Task DeleteDefinitionAsync_BuiltIn_Fails()
    {
        Result<bool> result = await BuildService(GetInMemoryOptions(), 1).DeleteDefinitionAsync(BuiltInDmxFixtureDefinitions.RgbDefinitionId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task DeleteDefinitionAsync_AnotherCompanysType_IsNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinition companyB = (await BuildService(options, 2).CreateCustomAsync(MovingHead())).Data!;

        Result<bool> result = await BuildService(options, 1).DeleteDefinitionAsync(companyB.Id);

        Assert.IsFalse(result.IsSuccess);
    }
}
