namespace Lanyard.Infrastructure.Models.Dmx;

// Pure channel-snapshot -> color mapping, deliberately free of any DB/service
// dependency so it can be unit tested directly and reused by both the live
// desk preview and the client-side scene-preview scrubber.
//
// It reads a fixture's channels by role (FixtureMode.Channels), so it works for any fixture
// definition. The three built-in modes produce exactly what the old DmxFixtureType switch did:
// Dimmer = white at the dimmer level, RGB = (R,G,B) at full brightness, RGB + Dimmer = (R,G,B) at
// the dimmer level.
public static class DmxFixtureColorMapper
{
    public readonly record struct FixtureColor(byte Red, byte Green, byte Blue, byte Brightness);

    // Pan and tilt as 0-100% of their range; null when the fixture has no such channel.
    public readonly record struct FixturePosition(double? PanPercent, double? TiltPercent);

    // What each additive emitter looks like at full output.
    private static readonly Dictionary<DmxChannelRole, (int R, int G, int B)> EmitterColors = new()
    {
        [DmxChannelRole.Red] = (255, 0, 0),
        [DmxChannelRole.Green] = (0, 255, 0),
        [DmxChannelRole.Blue] = (0, 0, 255),
        [DmxChannelRole.White] = (255, 255, 255),
        [DmxChannelRole.WarmWhite] = (255, 197, 143),
        [DmxChannelRole.ColdWhite] = (201, 226, 255),
        [DmxChannelRole.Amber] = (255, 191, 0),
        [DmxChannelRole.Lime] = (191, 255, 0),
        [DmxChannelRole.Uv] = (90, 0, 200),
        [DmxChannelRole.Indigo] = (75, 0, 130),
    };

    public static FixtureColor GetColor(DmxFixture fixture, IReadOnlyDictionary<int, byte> channelValues)
    {
        List<DmxFixtureModeChannel> channels = ChannelsOf(fixture);

        byte Get(DmxFixtureModeChannel channel) =>
            channelValues.TryGetValue(fixture.StartChannel + channel.Offset, out byte value) ? value : (byte)0;

        // Fine channels only add precision; a swatch doesn't need it.
        List<DmxFixtureModeChannel> coarse = channels.Where(c => c.CoarseOffset is null).ToList();

        bool hasEmitter = false;
        int red = 0, green = 0, blue = 0;

        foreach (DmxFixtureModeChannel channel in coarse)
        {
            if (EmitterColors.TryGetValue(channel.Role, out (int R, int G, int B) nominal))
            {
                hasEmitter = true;
                byte level = Get(channel);
                red += nominal.R * level / 255;
                green += nominal.G * level / 255;
                blue += nominal.B * level / 255;
            }
        }

        // Several emitters at full can add up past 255; scale back down by the brightest component
        // so the hue stays right.
        int max = Math.Max(red, Math.Max(green, blue));
        if (max > 255)
        {
            red = red * 255 / max;
            green = green * 255 / max;
            blue = blue * 255 / max;
        }

        // A filter (CMY flags or a colour wheel) tints whatever the emitters produce, or white
        // light when the fixture has no emitters (a discharge or tungsten source).
        int filterRed = 255, filterGreen = 255, filterBlue = 255;
        bool hasFilter = false;

        foreach (DmxFixtureModeChannel channel in coarse)
        {
            switch (channel.Role)
            {
                case DmxChannelRole.Cyan:
                    hasFilter = true;
                    filterRed = filterRed * (255 - Get(channel)) / 255;
                    break;
                case DmxChannelRole.Magenta:
                    hasFilter = true;
                    filterGreen = filterGreen * (255 - Get(channel)) / 255;
                    break;
                case DmxChannelRole.Yellow:
                    hasFilter = true;
                    filterBlue = filterBlue * (255 - Get(channel)) / 255;
                    break;
                case DmxChannelRole.ColorWheel:
                case DmxChannelRole.ColorMacro:
                // A shutter's "closed" range is black and its "open" range white.
                case DmxChannelRole.Strobe:
                    byte value = Get(channel);
                    DmxFixtureModeChannelColorRange? range = channel.ColorRanges
                        .FirstOrDefault(r => value >= r.DmxStart && value <= r.DmxEnd);

                    // An unmapped range (open, rainbow scroll...) leaves the colour as it is.
                    if (range != null && TryParseHex(range.ColorHex, out (int R, int G, int B) slot))
                    {
                        hasFilter = true;
                        filterRed = filterRed * slot.R / 255;
                        filterGreen = filterGreen * slot.G / 255;
                        filterBlue = filterBlue * slot.B / 255;
                    }
                    break;
            }
        }

        if (!hasEmitter)
        {
            // No colour channels at all: a plain dimmer, so white light.
            red = 255;
            green = 255;
            blue = 255;
        }

        if (hasFilter)
        {
            red = red * filterRed / 255;
            green = green * filterGreen / 255;
            blue = blue * filterBlue / 255;
        }

        DmxFixtureModeChannel? intensity = coarse.FirstOrDefault(c => c.Role == DmxChannelRole.Intensity);
        byte brightness = intensity != null ? Get(intensity) : (byte)255;

        return new FixtureColor((byte)red, (byte)green, (byte)blue, brightness);
    }

    // Moving-head position as a readout, not geometry: Lanyard doesn't know where a head is hung.
    // Coarse and fine channels combine into one 16-bit (or 24-bit) value.
    public static FixturePosition GetPosition(DmxFixture fixture, IReadOnlyDictionary<int, byte> channelValues)
    {
        List<DmxFixtureModeChannel> channels = ChannelsOf(fixture);

        return new FixturePosition(
            Percent(fixture, channels, DmxChannelRole.Pan, channelValues),
            Percent(fixture, channels, DmxChannelRole.Tilt, channelValues));
    }

    // True when a strobe channel is above zero and not in one of its plain open/closed shutter
    // ranges (a channel without ranges strobes at any non-zero value).
    public static bool IsStrobing(DmxFixture fixture, IReadOnlyDictionary<int, byte> channelValues) =>
        ChannelsOf(fixture).Any(c => c.Role == DmxChannelRole.Strobe
            && c.CoarseOffset is null
            && channelValues.TryGetValue(fixture.StartChannel + c.Offset, out byte value)
            && value > 0
            && !c.ColorRanges.Any(r => value >= r.DmxStart && value <= r.DmxEnd));

    private static double? Percent(DmxFixture fixture, List<DmxFixtureModeChannel> channels, DmxChannelRole role, IReadOnlyDictionary<int, byte> channelValues)
    {
        DmxFixtureModeChannel? coarse = channels.FirstOrDefault(c => c.Role == role && c.CoarseOffset is null);

        if (coarse == null)
        {
            return null;
        }

        List<DmxFixtureModeChannel> fines = channels
            .Where(c => c.CoarseOffset == coarse.Offset && c.FineLevel > 0)
            .OrderBy(c => c.FineLevel)
            .ToList();

        long value = Read(fixture, coarse, channelValues);
        foreach (DmxFixtureModeChannel fine in fines)
        {
            value = (value << 8) | Read(fixture, fine, channelValues);
        }

        long maxValue = (1L << (8 * (fines.Count + 1))) - 1;

        return Math.Round(value * 100.0 / maxValue, 1);
    }

    private static byte Read(DmxFixture fixture, DmxFixtureModeChannel channel, IReadOnlyDictionary<int, byte> channelValues) =>
        channelValues.TryGetValue(fixture.StartChannel + channel.Offset, out byte value) ? value : (byte)0;

    private static List<DmxFixtureModeChannel> ChannelsOf(DmxFixture fixture)
    {
        if (fixture.FixtureMode != null)
        {
            return fixture.FixtureMode.Channels.ToList();
        }

        // A fixture loaded without its mode (an older caller) still renders if its mode is built in.
        if (BuiltInDmxFixtureDefinitions.Modes.Any(m => m.Id == fixture.FixtureModeId))
        {
            return BuiltInDmxFixtureDefinitions.BuildMode(fixture.FixtureModeId).Channels.ToList();
        }

        return [];
    }

    private static bool TryParseHex(string hex, out (int R, int G, int B) color)
    {
        color = default;

        if (hex.Length != 7 || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return false;
        }

        color = ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }
}
