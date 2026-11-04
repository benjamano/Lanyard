using Lanyard.App.Components.Dmx;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components;

[TestClass]
public class DmxChannelPatchTests
{
    private static DmxFixture Fixture(string name, int start, Guid modeId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        StartChannel = start,
        FixtureModeId = modeId,
        FixtureMode = BuiltInDmxFixtureDefinitions.BuildMode(modeId),
        CreateByUserId = "u"
    };

    [TestMethod]
    public void Build_MapsEachFixtureChannelToItsAddressAndRole()
    {
        Dictionary<int, List<DmxChannelPatch>> patches = DmxChannelPatch.Build([Fixture("Par", 10, BuiltInDmxFixtureDefinitions.RgbDimmerModeId)]);

        CollectionAssert.AreEquivalent(new[] { 10, 11, 12, 13 }, patches.Keys.ToArray());
        Assert.AreEqual(DmxChannelRole.Red, patches[10].Single().Role);
        Assert.AreEqual(DmxChannelRole.Intensity, patches[13].Single().Role);
        Assert.AreEqual("Par", patches[13].Single().FixtureName);
    }

    [TestMethod]
    public void Build_DoublePatchedChannel_ListsBothFixtures()
    {
        Dictionary<int, List<DmxChannelPatch>> patches = DmxChannelPatch.Build(
        [
            Fixture("Wash", 1, BuiltInDmxFixtureDefinitions.RgbModeId),
            Fixture("Old wash", 3, BuiltInDmxFixtureDefinitions.RgbModeId),
        ]);

        CollectionAssert.AreEqual(new[] { "Wash", "Old wash" }, patches[3].Select(p => p.FixtureName).ToArray());
        Assert.AreEqual(1, patches[2].Count);
    }

    [TestMethod]
    public void Build_ChannelsPast512_AreLeftOut()
    {
        Dictionary<int, List<DmxChannelPatch>> patches = DmxChannelPatch.Build([Fixture("Legacy", 511, BuiltInDmxFixtureDefinitions.RgbModeId)]);

        CollectionAssert.AreEquivalent(new[] { 511, 512 }, patches.Keys.ToArray());
    }

    [TestMethod]
    public void Build_FixtureWithoutItsMode_ContributesNothing()
    {
        DmxFixture fixture = Fixture("Unknown", 1, BuiltInDmxFixtureDefinitions.RgbModeId);
        fixture.FixtureMode = null;

        Assert.AreEqual(0, DmxChannelPatch.Build([fixture]).Count);
    }
}
