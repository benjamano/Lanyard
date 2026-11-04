using System.IO.Compression;
using System.Text;
using Lanyard.Application.Services.Dmx.Ofl;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class OflCatalogueServiceTests
{
    private const string Snapshot = """
    {
      "source": { "repository": "https://github.com/OpenLightingProject/open-fixture-library", "commit": "abc123", "builtAt": "2026-10-12T00:00:00Z" },
      "manufacturers": { "cameo": { "name": "Cameo" }, "martin": { "name": "Martin" }, "lixada": { "name": "Lixada" } },
      "fixtures": {
        "cameo/flat-pro-18": { "name": "Flat Pro 18", "availableChannels": { "Red": { "capability": { "type": "ColorIntensity", "color": "Red" } } }, "modes": [ { "name": "1-channel", "channels": ["Red"] } ] },
        "martin/mac-aura": { "name": "MAC Aura", "availableChannels": { "Dimmer": { "capability": { "type": "Intensity" } } }, "modes": [ { "name": "Standard", "channels": ["Dimmer"] } ] },
        "martin/broken": { "name": "Broken" },
        "lixada/flat-par": { "name": "Flat Par", "redirectTo": "cameo/flat-pro-18", "reason": "SameAsDifferentBrand" }
      }
    }
    """;

    private static OflCatalogueService BuildService(string? snapshot = Snapshot) =>
        new(new OflFixtureParser(), NullLogger<OflCatalogueService>.Instance, () =>
        {
            if (snapshot == null)
            {
                return null;
            }

            MemoryStream compressed = new();
            using (GZipStream gzip = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(snapshot));
            }

            compressed.Position = 0;
            return compressed;
        });

    [TestMethod]
    public void Search_MatchesEveryWordAcrossManufacturerAndModel()
    {
        OflCatalogueService service = BuildService();

        Result<IReadOnlyList<OflCatalogueEntry>> result = service.Search("martin aura", 10);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual("martin/mac-aura", result.Data[0].Key);
        Assert.AreEqual("Martin", result.Data[0].Manufacturer);
        Assert.AreEqual(1, result.Data[0].Modes.Count);
    }

    [TestMethod]
    public void Search_EmptyQuery_ReturnsNothing()
    {
        Result<IReadOnlyList<OflCatalogueEntry>> result = BuildService().Search("  ", 10);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public void Load_SkipsInvalidFixtures_AndReportsTheSnapshotCommit()
    {
        OflCatalogueService service = BuildService();

        Assert.AreEqual(0, service.Search("broken", 10).Data!.Count);

        OflSnapshotInfo info = service.GetSnapshotInfo().Data!;
        Assert.AreEqual("abc123", info.Commit);
        Assert.AreEqual(3, info.FixtureCount);
    }

    [TestMethod]
    public void GetFixture_KnownKey_ReturnsTheParsedFixtureWithItsManufacturer()
    {
        Result<OflParsedFixture> result = BuildService().GetFixture("cameo/flat-pro-18");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Cameo", result.Data!.Manufacturer);
        Assert.AreEqual("Flat Pro 18", result.Data.Model);
    }

    [TestMethod]
    public void GetFixture_UnknownKey_Fails()
    {
        Assert.IsFalse(BuildService().GetFixture("nobody/nothing").IsSuccess);
    }

    [TestMethod]
    public void MissingSnapshot_FailsInsteadOfThrowing()
    {
        OflCatalogueService service = BuildService(null);

        Assert.IsFalse(service.Search("aura", 10).IsSuccess);
        Assert.IsFalse(service.GetFixture("martin/mac-aura").IsSuccess);
    }

    // The real embedded snapshot: loads, and finds a fixture we know is in it.
    [TestMethod]
    public void EmbeddedSnapshot_LoadsAndIsSearchable()
    {
        OflCatalogueService service = new(new OflFixtureParser(), NullLogger<OflCatalogueService>.Instance);

        Result<IReadOnlyList<OflCatalogueEntry>> result = service.Search("cameo flat pro 18", 5);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.Any(e => e.Key == "cameo/flat-pro-18"));
        Assert.IsTrue(service.GetSnapshotInfo().Data!.FixtureCount > 500);
    }

    [TestMethod]
    public void RebadgedFixture_IsFoundUnderItsOwnBrand_WithTheOriginalsChannels()
    {
        OflCatalogueService service = BuildService();

        Assert.AreEqual("lixada/flat-par", service.Search("lixada", 10).Data!.Single().Key);

        Result<OflParsedFixture> fixture = service.GetFixture("lixada/flat-par");

        Assert.IsTrue(fixture.IsSuccess, fixture.Error);
        Assert.AreEqual("Lixada", fixture.Data!.Manufacturer);
        Assert.AreEqual("Flat Par", fixture.Data.Model);
        Assert.AreEqual("lixada/flat-par", fixture.Data.Key);
        Assert.AreEqual(1, fixture.Data.Modes[0].ChannelCount);
    }
}
