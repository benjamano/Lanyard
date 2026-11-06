using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxDimmableChannelsTests
{
    private static DmxFixture Fixture(int start, params DmxChannelRole[] roles) => new()
    {
        Id = Guid.NewGuid(),
        Name = "F",
        CreateByUserId = "test",
        StartChannel = start,
        FixtureMode = new DmxFixtureMode
        {
            Name = "m",
            ChannelCount = roles.Length,
            Channels = roles.Select((role, i) => new DmxFixtureModeChannel { Offset = i, Role = role, Label = role.ToString() }).ToList()
        }
    };

    [TestMethod]
    public void NoFixtures_EverythingIsDimmable()
    {
        bool[] dimmable = DmxDimmableChannels.Build([]);

        Assert.IsTrue(dimmable.Skip(1).All(d => d));
    }

    [TestMethod]
    public void DoublePatch_OnlyDimmableIfEveryFixtureAgrees()
    {
        bool[] dimmable = DmxDimmableChannels.Build([
            Fixture(1, DmxChannelRole.Intensity),
            Fixture(1, DmxChannelRole.Pan)
        ]);

        Assert.IsFalse(dimmable[1]);
    }

    [TestMethod]
    public void FootprintPast512_IsIgnoredNotThrown()
    {
        bool[] dimmable = DmxDimmableChannels.Build([Fixture(511, DmxChannelRole.Pan, DmxChannelRole.Tilt, DmxChannelRole.Gobo)]);

        Assert.IsFalse(dimmable[511]);
        Assert.IsFalse(dimmable[512]);
    }

    [TestMethod]
    [DataRow((byte)255, (byte)255, (byte)255)]
    [DataRow((byte)200, (byte)128, (byte)100)]
    [DataRow((byte)1, (byte)255, (byte)1)]
    [DataRow((byte)255, (byte)0, (byte)0)]
    public void Output_ScalesWithRounding(byte programmed, byte master, byte expected)
    {
        Assert.AreEqual(expected, DmxDimmableChannels.Output(programmed, isDimmable: true, master, isBlackout: false));
    }

    [TestMethod]
    public void Output_NotDimmable_IsUntouchedByMasterOrBlackout()
    {
        Assert.AreEqual((byte)90, DmxDimmableChannels.Output(90, isDimmable: false, grandMaster: 0, isBlackout: true));
    }
}
