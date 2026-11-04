using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxFixtureColorMapperTests
{
    // The old fixture types, now built-in modes. These first four tests predate fixture definitions
    // and are kept unchanged as parity tests: existing fixtures must render exactly as before.
    private static DmxFixture MakeFixture(DmxFixtureType type, int startChannel = 10)
    {
        Guid modeId = BuiltInDmxFixtureDefinitions.ModeIdFor(type);

        return new DmxFixture
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Name = "Test fixture",
            StartChannel = startChannel,
            FixtureModeId = modeId,
            FixtureMode = BuiltInDmxFixtureDefinitions.BuildMode(modeId),
            CreateByUserId = "test-user"
        };
    }

    private static DmxFixture MakeFixture(int startChannel, params DmxFixtureModeChannel[] channels)
    {
        Guid modeId = Guid.NewGuid();

        foreach (DmxFixtureModeChannel channel in channels)
        {
            channel.ModeId = modeId;
        }

        return new DmxFixture
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Name = "Test fixture",
            StartChannel = startChannel,
            FixtureModeId = modeId,
            FixtureMode = new DmxFixtureMode { Id = modeId, Name = "Test", ChannelCount = channels.Length, Channels = channels.ToList() },
            CreateByUserId = "test-user"
        };
    }

    private static DmxFixtureModeChannel Channel(int offset, DmxChannelRole role, int? coarseOffset = null, byte fineLevel = 0) => new()
    {
        Id = Guid.NewGuid(),
        Offset = offset,
        Label = role.ToString(),
        Role = role,
        CoarseOffset = coarseOffset,
        FineLevel = fineLevel
    };

    private static DmxFixtureModeChannel Wheel(int offset, DmxChannelRole role, params (byte Start, byte End, string Hex)[] ranges)
    {
        DmxFixtureModeChannel channel = Channel(offset, role);
        channel.ColorRanges = ranges
            .Select(r => new DmxFixtureModeChannelColorRange { Id = Guid.NewGuid(), DmxStart = r.Start, DmxEnd = r.End, ColorHex = r.Hex })
            .ToList();

        return channel;
    }

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

    [TestMethod]
    public void GetColor_FixtureLoadedWithoutItsMode_StillRendersBuiltIns()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.Rgb, startChannel: 1);
        fixture.FixtureMode = null;

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 10, [2] = 20, [3] = 30 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(10, 20, 30, 255), color);
    }

    [TestMethod]
    public void GetColor_RgbwWithOnlyWhite_IsWhite()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Red), Channel(1, DmxChannelRole.Green), Channel(2, DmxChannelRole.Blue), Channel(3, DmxChannelRole.White));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [4] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 255, 255, 255), color);
    }

    [TestMethod]
    public void GetColor_RedPlusWhiteAtFull_ScalesDownKeepingHue()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Red), Channel(1, DmxChannelRole.Green), Channel(2, DmxChannelRole.Blue), Channel(3, DmxChannelRole.White));

        // Red 255 + white 255 = (510, 255, 255) -> scaled by 255/510.
        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 255, [4] = 255 });

        Assert.AreEqual(255, color.Red);
        Assert.AreEqual(127, color.Green);
        Assert.AreEqual(127, color.Blue);
    }

    [TestMethod]
    public void GetColor_AmberChannel_IsAmber()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Red), Channel(1, DmxChannelRole.Green), Channel(2, DmxChannelRole.Blue), Channel(3, DmxChannelRole.Amber));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [4] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 191, 0, 255), color);
    }

    [TestMethod]
    public void GetColor_WarmAndColdWhite_AreTinted()
    {
        DmxFixture warm = MakeFixture(1, Channel(0, DmxChannelRole.WarmWhite), Channel(1, DmxChannelRole.ColdWhite));

        DmxFixtureColorMapper.FixtureColor warmColor = DmxFixtureColorMapper.GetColor(warm, new Dictionary<int, byte> { [1] = 255 });
        DmxFixtureColorMapper.FixtureColor coldColor = DmxFixtureColorMapper.GetColor(warm, new Dictionary<int, byte> { [2] = 255 });

        Assert.IsTrue(warmColor.Red > warmColor.Blue, "Warm white should lean red.");
        Assert.IsTrue(coldColor.Blue > coldColor.Red, "Cold white should lean blue.");
    }

    [TestMethod]
    public void GetColor_EmittersAllOff_IsBlackNotWhite()
    {
        DmxFixture fixture = MakeFixture(1, Channel(0, DmxChannelRole.Intensity), Channel(1, DmxChannelRole.Red), Channel(2, DmxChannelRole.Green), Channel(3, DmxChannelRole.Blue));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(0, 0, 0, 255), color);
    }

    [TestMethod]
    public void GetColor_Cmy_SubtractsFromWhite()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Intensity), Channel(1, DmxChannelRole.Cyan), Channel(2, DmxChannelRole.Magenta), Channel(3, DmxChannelRole.Yellow));

        // Full magenta + full yellow leaves red.
        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 200, [3] = 255, [4] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 0, 0, 200), color);
    }

    [TestMethod]
    public void GetColor_ColorWheel_UsesTheSlotForTheValue()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Intensity),
            Wheel(1, DmxChannelRole.ColorWheel, (0, 9, "#FFFFFF"), (10, 19, "#00FF00"), (20, 29, "#0000FF")));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 255, [2] = 15 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(0, 255, 0, 255), color);
    }

    [TestMethod]
    public void GetColor_ColorWheelValueInAnUnmappedRange_LeavesTheLightWhite()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Intensity),
            Wheel(1, DmxChannelRole.ColorWheel, (10, 19, "#00FF00")));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 255, [2] = 200 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 255, 255, 255), color);
    }

    [TestMethod]
    public void GetColor_ColorMacroOnRgbFixture_TintsTheEmitters()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Red), Channel(1, DmxChannelRole.Green), Channel(2, DmxChannelRole.Blue),
            Wheel(3, DmxChannelRole.ColorMacro, (1, 255, "#FF0000")));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 255, [2] = 255, [3] = 255, [4] = 100 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 0, 0, 255), color);
    }

    [TestMethod]
    public void GetColor_FineChannels_DoNotCountAsExtraEmitters()
    {
        DmxFixture fixture = MakeFixture(1,
            Channel(0, DmxChannelRole.Intensity), Channel(1, DmxChannelRole.Intensity, coarseOffset: 0, fineLevel: 1),
            Channel(2, DmxChannelRole.Red), Channel(3, DmxChannelRole.Red, coarseOffset: 2, fineLevel: 1));

        DmxFixtureColorMapper.FixtureColor color = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 100, [2] = 255, [3] = 128, [4] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(128, 0, 0, 100), color);
    }

    [TestMethod]
    public void GetPosition_CombinesCoarseAndFineInto16Bit()
    {
        DmxFixture fixture = MakeFixture(100,
            Channel(0, DmxChannelRole.Pan), Channel(1, DmxChannelRole.Pan, coarseOffset: 0, fineLevel: 1),
            Channel(2, DmxChannelRole.Tilt));

        DmxFixtureColorMapper.FixturePosition position = DmxFixtureColorMapper.GetPosition(fixture, new Dictionary<int, byte> { [100] = 128, [101] = 0, [102] = 255 });

        Assert.AreEqual(50.0, position.PanPercent!.Value, 0.1);
        Assert.AreEqual(100.0, position.TiltPercent!.Value, 0.1);
    }

    [TestMethod]
    public void GetPosition_NoPanOrTilt_IsNull()
    {
        DmxFixture fixture = MakeFixture(DmxFixtureType.Rgb);

        DmxFixtureColorMapper.FixturePosition position = DmxFixtureColorMapper.GetPosition(fixture, new Dictionary<int, byte>());

        Assert.IsNull(position.PanPercent);
        Assert.IsNull(position.TiltPercent);
    }

    [TestMethod]
    public void IsStrobing_TrueOnlyWhenStrobeChannelIsAboveZero()
    {
        DmxFixture fixture = MakeFixture(1, Channel(0, DmxChannelRole.Intensity), Channel(1, DmxChannelRole.Strobe));

        Assert.IsFalse(DmxFixtureColorMapper.IsStrobing(fixture, new Dictionary<int, byte> { [1] = 255 }));
        Assert.IsTrue(DmxFixtureColorMapper.IsStrobing(fixture, new Dictionary<int, byte> { [2] = 10 }));
    }

    [TestMethod]
    public void GetColor_ClosedShutter_IsBlack_OpenShutterLeavesTheColour()
    {
        DmxFixture fixture = MakeFixture(1,
            Wheel(0, DmxChannelRole.Strobe, (0, 19, "#000000"), (20, 24, "#FFFFFF")),
            Channel(1, DmxChannelRole.Intensity),
            Channel(2, DmxChannelRole.Red));

        DmxFixtureColorMapper.FixtureColor closed = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 0, [2] = 255, [3] = 255 });
        DmxFixtureColorMapper.FixtureColor open = DmxFixtureColorMapper.GetColor(fixture, new Dictionary<int, byte> { [1] = 22, [2] = 255, [3] = 255 });

        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(0, 0, 0, 255), closed);
        Assert.AreEqual(new DmxFixtureColorMapper.FixtureColor(255, 0, 0, 255), open);
    }

    [TestMethod]
    public void IsStrobing_ShutterWithRanges_OnlyOutsideTheOpenAndClosedRanges()
    {
        DmxFixture fixture = MakeFixture(1, Wheel(0, DmxChannelRole.Strobe, (0, 19, "#000000"), (20, 24, "#FFFFFF")));

        Assert.IsFalse(DmxFixtureColorMapper.IsStrobing(fixture, new Dictionary<int, byte> { [1] = 10 }), "Closed.");
        Assert.IsFalse(DmxFixtureColorMapper.IsStrobing(fixture, new Dictionary<int, byte> { [1] = 22 }), "Open.");
        Assert.IsTrue(DmxFixtureColorMapper.IsStrobing(fixture, new Dictionary<int, byte> { [1] = 100 }));
    }
}
