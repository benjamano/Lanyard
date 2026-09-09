using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxFixtureColorMapperTests
{
    private static DmxFixture MakeFixture(DmxFixtureType type, int startChannel = 10) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Name = "Test fixture",
        StartChannel = startChannel,
        FixtureType = type,
        CreateByUserId = "test-user"
    };

    [TestMethod]
    public void GetColor_Dimmer_ReadsOnlyOffsetZero_AsFullWhiteScaledByBrightness()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.Dimmer, startChannel: 10);

        Dictionary<int, byte> channelValues = new()
        {
            [10] = 128,
            [11] = 200, // would be Red on an RGB fixture; must be ignored for Dimmer
            [12] = 200,
        };

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, channelValues);

        Assert.AreEqual(255, color.Red);
        Assert.AreEqual(255, color.Green);
        Assert.AreEqual(255, color.Blue);
        Assert.AreEqual(128, color.Brightness);
    }

    [TestMethod]
    public void GetColor_Rgb_ReadsOffsetsZeroToTwo_AndFullBrightness()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.Rgb, startChannel: 5);

        Dictionary<int, byte> channelValues = new()
        {
            [5] = 10,
            [6] = 20,
            [7] = 30,
        };

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, channelValues);

        Assert.AreEqual(10, color.Red);
        Assert.AreEqual(20, color.Green);
        Assert.AreEqual(30, color.Blue);
        Assert.AreEqual(255, color.Brightness);
    }

    [TestMethod]
    public void GetColor_RgbDimmer_ReadsOffsetsZeroToThree()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.RgbDimmer, startChannel: 1);

        Dictionary<int, byte> channelValues = new()
        {
            [1] = 10,
            [2] = 20,
            [3] = 30,
            [4] = 40,
        };

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, channelValues);

        Assert.AreEqual(10, color.Red);
        Assert.AreEqual(20, color.Green);
        Assert.AreEqual(30, color.Blue);
        Assert.AreEqual(40, color.Brightness);
    }

    [TestMethod]
    public void GetColor_MissingChannelsInSnapshot_DefaultToZero()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.RgbDimmer, startChannel: 100);

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte>());

        Assert.AreEqual(0, color.Red);
        Assert.AreEqual(0, color.Green);
        Assert.AreEqual(0, color.Blue);
        Assert.AreEqual(0, color.Brightness);
    }
}
