using System.Text;
using Lanyard.Application.Services.Dmx.Ofl;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

// Fixture JSON below follows OFL's fixture format (docs/fixture-format.md), trimmed from real
// definitions in the library (Cameo Flat Pro 18, Martin MAC Aura) plus small made-up ones for the
// cases those don't cover.
[TestClass]
public class OflFixtureParserTests
{
    // Trimmed from fixtures/cameo/flat-pro-18.json.
    private const string FlatPro18 = """
    {
      "name": "Flat Pro 18",
      "meta": { "authors": ["Felix Edelmann"], "createDate": "2017-07-14", "lastModifyDate": "2018-08-09" },
      "availableChannels": {
        "Dimmer": { "capability": { "type": "Intensity" } },
        "Strobe": { "capabilities": [
          { "dmxRange": [0, 9], "type": "NoFunction" },
          { "dmxRange": [10, 255], "type": "ShutterStrobe", "shutterEffect": "Strobe", "speedStart": "slow", "speedEnd": "fast" } ] },
        "Red": { "capability": { "type": "ColorIntensity", "color": "Red" } },
        "Green": { "capability": { "type": "ColorIntensity", "color": "Green" } },
        "Blue": { "capability": { "type": "ColorIntensity", "color": "Blue" } },
        "White": { "capability": { "type": "ColorIntensity", "color": "White" } },
        "Amber": { "capability": { "type": "ColorIntensity", "color": "Amber" } }
      },
      "modes": [
        { "name": "3-channel 2", "shortName": "3ch2", "channels": ["Red", "Green", "Blue"] },
        { "name": "5-channel", "shortName": "5ch", "channels": ["Red", "Green", "Blue", "White", "Amber"] },
        { "name": "8-channel", "shortName": "8ch", "channels": ["Red", "Green", "Blue", "White", "Amber", "Dimmer", "Strobe", null] }
      ]
    }
    """;

    // Trimmed from fixtures/martin/mac-aura.json: fine pan/tilt, a shutter, a colour-preset channel.
    private const string MacAura = """
    {
      "name": "MAC Aura",
      "meta": { "lastModifyDate": "2019-01-01" },
      "availableChannels": {
        "Shutter": { "capabilities": [
          { "dmxRange": [0, 19], "type": "ShutterStrobe", "shutterEffect": "Closed" },
          { "dmxRange": [20, 24], "type": "ShutterStrobe", "shutterEffect": "Open" },
          { "dmxRange": [25, 255], "type": "ShutterStrobe", "shutterEffect": "Strobe" } ] },
        "Beam dimmer": { "capability": { "type": "Intensity" } },
        "Pan": { "fineChannelAliases": ["Pan fine"], "defaultValue": "50%", "capability": { "type": "Pan", "angleStart": "0deg", "angleEnd": "540deg" } },
        "Tilt": { "fineChannelAliases": ["Tilt fine"], "defaultValue": "50%", "capability": { "type": "Tilt", "angleStart": "0deg", "angleEnd": "232deg" } },
        "Beam color wheel effect": { "capabilities": [
          { "dmxRange": [0, 9], "type": "NoFunction" },
          { "dmxRange": [10, 14], "type": "ColorPreset", "colors": ["#ffeb34"] },
          { "dmxRange": [15, 255], "type": "ColorPreset", "colors": ["#d68630"] } ] }
      },
      "modes": [
        { "name": "Standard", "channels": ["Shutter", "Beam dimmer", "Pan", "Pan fine", "Tilt", "Tilt fine", "Beam color wheel effect"] }
      ]
    }
    """;

    private const string ColorWheelFixture = """
    {
      "name": "Wheel Spot",
      "availableChannels": {
        "Color Wheel": { "capabilities": [
          { "dmxRange": [0, 9], "type": "WheelSlot", "slotNumber": 1 },
          { "dmxRange": [10, 19], "type": "WheelSlot", "slotNumber": 2 },
          { "dmxRange": [20, 29], "type": "WheelSlot", "slotNumber": 2.5 },
          { "dmxRange": [30, 255], "type": "WheelRotation", "speedStart": "slow CW", "speedEnd": "fast CW" } ] },
        "Dimmer": { "capability": { "type": "Intensity" } }
      },
      "wheels": {
        "Color Wheel": { "slots": [
          { "type": "Open" },
          { "type": "Color", "name": "Red", "colors": ["#ff0000"] },
          { "type": "Color", "name": "Blue", "colors": ["#0000ff"] } ] }
      },
      "modes": [ { "name": "2-channel", "channels": ["Dimmer", "Color Wheel"] } ]
    }
    """;

    // A pixel bar: one plain mode and one that repeats template channels per pixel.
    private const string PixelBar = """
    {
      "name": "Pixel Bar",
      "availableChannels": { "Master": { "capability": { "type": "Intensity" } } },
      "templateChannels": { "Red $pixelKey": { "capability": { "type": "ColorIntensity", "color": "Red" } } },
      "matrix": { "pixelCount": [4, 1, 1] },
      "modes": [
        { "name": "1-channel", "channels": ["Master"] },
        { "name": "Pixel", "channels": ["Master", { "insert": "matrixChannels", "repeatFor": "eachPixelXYZ", "channelOrder": "perPixel", "templateChannels": ["Red $pixelKey"] }] },
        { "name": "Pixel resolved", "channels": ["Master", "Red 1", "Red 2"] }
      ]
    }
    """;

    private static Result<OflParsedFixture> Parse(string json, string? key = "test/fixture", string? manufacturer = "Test Co") =>
        new OflFixtureParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), key, manufacturer);

    [TestMethod]
    public void Parse_RgbwaPar_MapsModesAndColourRoles()
    {
        Result<OflParsedFixture> result = Parse(FlatPro18, "cameo/flat-pro-18", "Cameo");

        Assert.IsTrue(result.IsSuccess, result.Error);
        OflParsedFixture fixture = result.Data!;
        Assert.AreEqual("Cameo", fixture.Manufacturer);
        Assert.AreEqual("Flat Pro 18", fixture.Model);
        Assert.AreEqual("cameo/flat-pro-18", fixture.Key);
        Assert.AreEqual("2018-08-09", fixture.Revision);
        Assert.AreEqual(3, fixture.Modes.Count);

        OflParsedMode fiveChannel = fixture.Modes[1];
        Assert.AreEqual("5-channel", fiveChannel.Name);
        Assert.AreEqual(5, fiveChannel.ChannelCount);
        CollectionAssert.AreEqual(
            new[] { DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue, DmxChannelRole.White, DmxChannelRole.Amber },
            fiveChannel.Channels.Select(c => c.Role).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, fiveChannel.Channels.Select(c => c.Offset).ToArray());
    }

    [TestMethod]
    public void Parse_NullModeEntry_IsAnUnusedSlotThatStillCountsTowardsTheFootprint()
    {
        OflParsedMode eightChannel = Parse(FlatPro18).Data!.Modes[2];

        Assert.AreEqual(8, eightChannel.ChannelCount);
        Assert.AreEqual(DmxChannelRole.NoFunction, eightChannel.Channels[7].Role);
        Assert.AreEqual(DmxChannelRole.Intensity, eightChannel.Channels[5].Role);
        Assert.AreEqual(DmxChannelRole.Strobe, eightChannel.Channels[6].Role, "A strobe channel that's mostly strobe is Strobe, not NoFunction.");
    }

    [TestMethod]
    public void Parse_FineChannels_PointAtTheirCoarseChannel()
    {
        OflParsedMode mode = Parse(MacAura).Data!.Modes[0];

        OflParsedChannel pan = mode.Channels[2];
        OflParsedChannel panFine = mode.Channels[3];
        OflParsedChannel tiltFine = mode.Channels[5];

        Assert.AreEqual(DmxChannelRole.Pan, pan.Role);
        Assert.IsNull(pan.CoarseOffset);
        Assert.AreEqual(128, pan.DefaultValue, "\"50%\" default.");
        Assert.AreEqual(DmxChannelRole.Pan, panFine.Role);
        Assert.AreEqual(2, panFine.CoarseOffset);
        Assert.AreEqual((byte)1, panFine.FineLevel);
        Assert.AreEqual(4, tiltFine.CoarseOffset);
    }

    [TestMethod]
    public void Parse_Shutter_RecordsClosedAsBlackAndOpenAsWhite()
    {
        OflParsedChannel shutter = Parse(MacAura).Data!.Modes[0].Channels[0];

        Assert.AreEqual(DmxChannelRole.Strobe, shutter.Role);
        Assert.AreEqual(2, shutter.ColorRanges.Count);
        Assert.AreEqual(new OflParsedColorRange(0, 19, "#000000"), shutter.ColorRanges[0]);
        Assert.AreEqual(new OflParsedColorRange(20, 24, "#FFFFFF"), shutter.ColorRanges[1]);
    }

    [TestMethod]
    public void Parse_ColorPresetChannel_IsAColourMacroWithRanges()
    {
        OflParsedChannel macro = Parse(MacAura).Data!.Modes[0].Channels[6];

        Assert.AreEqual(DmxChannelRole.ColorMacro, macro.Role);
        Assert.AreEqual(2, macro.ColorRanges.Count);
        Assert.AreEqual("#D68630", macro.ColorRanges[1].ColorHex);
    }

    [TestMethod]
    public void Parse_ColourWheel_MapsWholeSlotsAndSkipsSplitOnes()
    {
        OflParsedChannel wheel = Parse(ColorWheelFixture).Data!.Modes[0].Channels[1];

        Assert.AreEqual(DmxChannelRole.ColorWheel, wheel.Role);
        Assert.AreEqual(2, wheel.ColorRanges.Count, "The half-way slot (2.5) has no single colour.");
        Assert.AreEqual(new OflParsedColorRange(0, 9, "#FFFFFF"), wheel.ColorRanges[0]);
        Assert.AreEqual(new OflParsedColorRange(10, 19, "#FF0000"), wheel.ColorRanges[1]);
    }

    [TestMethod]
    public void Parse_PixelModes_AreMarkedUnsupportedButPlainModesStillWork()
    {
        OflParsedFixture fixture = Parse(PixelBar).Data!;

        Assert.IsTrue(fixture.Modes[0].IsSupported);
        Assert.IsFalse(fixture.Modes[1].IsSupported);
        StringAssert.Contains(fixture.Modes[1].UnsupportedReason, "Pixel");
    }

    [TestMethod]
    public void Parse_FixedModeListingResolvedTemplateChannels_IsSupported()
    {
        OflParsedMode resolved = Parse(PixelBar).Data!.Modes[2];

        Assert.IsTrue(resolved.IsSupported, resolved.UnsupportedReason);
        CollectionAssert.AreEqual(
            new[] { DmxChannelRole.Intensity, DmxChannelRole.Red, DmxChannelRole.Red },
            resolved.Channels.Select(c => c.Role).ToArray());
        Assert.AreEqual("Red 2", resolved.Channels[2].Label);
    }

    [TestMethod]
    public void Parse_TemplateFineAlias_PointsAtItsResolvedCoarseChannel()
    {
        string json = """
        {
          "name": "Pixel Spot",
          "availableChannels": {},
          "templateChannels": { "Pan $pixelKey": { "fineChannelAliases": ["Pan $pixelKey fine"], "capability": { "type": "Pan", "angleStart": "0deg", "angleEnd": "540deg" } } },
          "modes": [ { "name": "Fixed", "channels": ["Pan A", "Pan A fine", "Pan B"] } ]
        }
        """;

        OflParsedMode mode = Parse(json).Data!.Modes[0];

        Assert.IsTrue(mode.IsSupported, mode.UnsupportedReason);
        Assert.AreEqual(0, mode.Channels[1].CoarseOffset);
        Assert.AreEqual((byte)1, mode.Channels[1].FineLevel);
        Assert.AreEqual(DmxChannelRole.Pan, mode.Channels[2].Role);
        Assert.IsNull(mode.Channels[2].CoarseOffset);
    }

    [TestMethod]
    public void Parse_LongAndControlCharacterNames_AreCleaned()
    {
        string json = FlatPro18.Replace("\"name\": \"Flat Pro 18\"", $"\"name\": \"  Flat\\u0007 {new string('x', 300)}\"");

        OflParsedFixture fixture = Parse(json).Data!;

        Assert.AreEqual(OflFixtureParser.MaxNameLength, fixture.Model.Length);
        Assert.IsTrue(fixture.Model.StartsWith("Flat x"));
    }

    [TestMethod]
    public void Parse_FileOverTheSizeLimit_Fails()
    {
        string json = FlatPro18.Replace("\"name\": \"Flat Pro 18\"", $"\"name\": \"Flat Pro 18\", \"padding\": \"{new string('x', OflFixtureParser.MaxFileBytes)}\"");

        Result<OflParsedFixture> result = Parse(json);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "too big");
    }

    [TestMethod]
    public void Parse_DeeplyNestedJson_Fails()
    {
        string json = "{\"name\":\"Deep\",\"x\":" + new string('[', 100) + new string(']', 100) + "}";

        Assert.IsFalse(Parse(json).IsSuccess);
    }

    [TestMethod]
    public void Parse_MalformedJson_Fails()
    {
        Assert.IsFalse(Parse("{ \"name\": ").IsSuccess);
        Assert.IsFalse(Parse("[]").IsSuccess);
        Assert.IsFalse(Parse("not json at all").IsSuccess);
    }

    [TestMethod]
    public void Parse_MissingNameChannelsOrModes_Fails()
    {
        Assert.IsFalse(Parse("""{ "availableChannels": {}, "modes": [] }""").IsSuccess);
        Assert.IsFalse(Parse("""{ "name": "X", "modes": [ { "name": "m", "channels": [] } ] }""").IsSuccess);
        Assert.IsFalse(Parse("""{ "name": "X", "availableChannels": {} }""").IsSuccess);
        Assert.IsFalse(Parse("""{ "name": "X", "availableChannels": {}, "modes": [] }""").IsSuccess);
    }

    [TestMethod]
    public void Parse_TooManyModes_Fails()
    {
        string modes = string.Join(",", Enumerable.Range(0, OflFixtureParser.MaxModes + 1).Select(i => $"{{\"name\":\"m{i}\",\"channels\":[\"D\"]}}"));
        string json = $"{{\"name\":\"X\",\"availableChannels\":{{\"D\":{{\"capability\":{{\"type\":\"Intensity\"}}}}}},\"modes\":[{modes}]}}";

        Assert.IsFalse(Parse(json).IsSuccess);
    }

    [TestMethod]
    public void Parse_TooManyChannels_Fails()
    {
        string channels = string.Join(",", Enumerable.Range(0, OflFixtureParser.MaxAvailableChannels + 1).Select(i => $"\"c{i}\":{{\"capability\":{{\"type\":\"Generic\"}}}}"));
        string json = $"{{\"name\":\"X\",\"availableChannels\":{{{channels}}},\"modes\":[{{\"name\":\"m\",\"channels\":[\"c0\"]}}]}}";

        Assert.IsFalse(Parse(json).IsSuccess);
    }

    [TestMethod]
    public void Parse_ModeWiderThanAUniverse_IsUnsupported()
    {
        string channels = string.Join(",", Enumerable.Repeat("\"D\"", 513));
        string json = $"{{\"name\":\"X\",\"availableChannels\":{{\"D\":{{\"capability\":{{\"type\":\"Intensity\"}}}}}},\"modes\":[{{\"name\":\"huge\",\"channels\":[{channels}]}}]}}";

        OflParsedMode mode = Parse(json).Data!.Modes[0];

        Assert.IsFalse(mode.IsSupported);
        Assert.AreEqual(513, mode.ChannelCount);
    }

    [TestMethod]
    public void Parse_UndefinedChannelKeyInAMode_MakesThatModeUnsupported()
    {
        string json = """{ "name": "X", "availableChannels": { "D": { "capability": { "type": "Intensity" } } }, "modes": [ { "name": "ok", "channels": ["D"] }, { "name": "bad", "channels": ["D", "Nope"] } ] }""";

        OflParsedFixture fixture = Parse(json).Data!;

        Assert.IsTrue(fixture.Modes[0].IsSupported);
        Assert.IsFalse(fixture.Modes[1].IsSupported);
    }

    [TestMethod]
    public void Parse_InvalidHexColour_IsIgnored()
    {
        string json = """{ "name": "X", "availableChannels": { "C": { "capabilities": [ { "dmxRange": [0, 127], "type": "ColorPreset", "colors": ["javascript:alert(1)"] }, { "dmxRange": [128, 255], "type": "ColorPreset", "colors": ["#00ff00"] } ] } }, "modes": [ { "name": "m", "channels": ["C"] } ] }""";

        OflParsedChannel channel = Parse(json).Data!.Modes[0].Channels[0];

        Assert.AreEqual(1, channel.ColorRanges.Count);
        Assert.AreEqual("#00FF00", channel.ColorRanges[0].ColorHex);
    }

    [TestMethod]
    public void Parse_SixteenBitRanges_AreScaledToEightBit()
    {
        string json = """{ "name": "X", "availableChannels": { "Shutter": { "fineChannelAliases": ["Shutter fine"], "capabilities": [ { "dmxRange": [0, 5119], "type": "ShutterStrobe", "shutterEffect": "Closed" }, { "dmxRange": [5120, 65535], "type": "ShutterStrobe", "shutterEffect": "Strobe" } ] } }, "modes": [ { "name": "m", "channels": ["Shutter", "Shutter fine"] } ] }""";

        OflParsedChannel shutter = Parse(json).Data!.Modes[0].Channels[0];

        Assert.AreEqual(new OflParsedColorRange(0, 19, "#000000"), shutter.ColorRanges[0]);
    }
}
