namespace Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// Fixture-level controls for the desk's fixture cards: read and set a light's brightness, colour
/// and position as a person thinks of them, turned into raw channel writes. Pure, so it's unit
/// tested; the inverse of <see cref="DmxFixtureColorMapper"/> for the colour it can set.
/// </summary>
public static class DmxFixtureControls
{
    public const int UniverseSize = 512;

    /// <summary>The address of the fixture's coarse channel with this role, or null when it has none.</summary>
    public static int? AddressOf(DmxFixture fixture, DmxChannelRole role)
    {
        DmxFixtureModeChannel? channel = fixture.FixtureMode?.Channels
            .Where(c => c.Role == role && c.FineLevel == 0)
            .OrderBy(c => c.Offset)
            .FirstOrDefault();

        if (channel == null)
        {
            return null;
        }

        int address = fixture.StartChannel + channel.Offset;

        return address is >= 1 and <= UniverseSize ? address : null;
    }

    public static bool Has(DmxFixture fixture, DmxChannelRole role) => AddressOf(fixture, role) != null;

    /// <summary>True when there's a red, green or blue emitter (or CMY) to set a colour with.</summary>
    public static bool HasColour(DmxFixture fixture) =>
        Has(fixture, DmxChannelRole.Red) || Has(fixture, DmxChannelRole.Green) || Has(fixture, DmxChannelRole.Blue)
        || Has(fixture, DmxChannelRole.Cyan) || Has(fixture, DmxChannelRole.Magenta) || Has(fixture, DmxChannelRole.Yellow);

    private static IEnumerable<int> EmitterAddresses(DmxFixture fixture) =>
        (fixture.FixtureMode?.Channels ?? [])
            .Where(c => c.FineLevel == 0 && DmxDimmableChannels.IsEmitter(c.Role))
            .Select(c => fixture.StartChannel + c.Offset)
            .Where(a => a is >= 1 and <= UniverseSize);

    /// <summary>
    /// How bright the light is, 0-255: its Intensity channel, or for a fixture without one, its
    /// brightest emitter. Null when it has neither (a scanner with only a gobo, say).
    /// </summary>
    public static byte? GetIntensity(DmxFixture fixture, IReadOnlyDictionary<int, byte> values)
    {
        if (AddressOf(fixture, DmxChannelRole.Intensity) is int address)
        {
            return values.GetValueOrDefault(address);
        }

        List<int> emitters = EmitterAddresses(fixture).ToList();

        return emitters.Count == 0 ? null : emitters.Max(a => values.GetValueOrDefault(a));
    }

    /// <summary>
    /// The writes that set the light's brightness. With an Intensity channel that's the one write.
    /// Without, the emitters are scaled together so the colour keeps its hue; an all-dark fixture
    /// brightened that way comes up white.
    /// </summary>
    public static List<DmxChannel> SetIntensity(DmxFixture fixture, IReadOnlyDictionary<int, byte> values, byte level)
    {
        if (AddressOf(fixture, DmxChannelRole.Intensity) is int address)
        {
            return [new DmxChannel { Address = address, Value = level }];
        }

        List<int> emitters = EmitterAddresses(fixture).ToList();
        byte current = emitters.Count == 0 ? (byte)0 : emitters.Max(a => values.GetValueOrDefault(a));

        return emitters
            .Select(a => new DmxChannel
            {
                Address = a,
                Value = current == 0 ? level : (byte)Math.Round(values.GetValueOrDefault(a) * (level / (double)current))
            })
            .ToList();
    }

    /// <summary>The colour the fixture's red/green/blue (white added in) or CMY channels make, before brightness.</summary>
    public static (byte R, byte G, byte B) GetColour(DmxFixture fixture, IReadOnlyDictionary<int, byte> values)
    {
        byte Read(DmxChannelRole role) => AddressOf(fixture, role) is int a ? values.GetValueOrDefault(a) : (byte)0;

        if (Has(fixture, DmxChannelRole.Red) || Has(fixture, DmxChannelRole.Green) || Has(fixture, DmxChannelRole.Blue))
        {
            byte white = Read(DmxChannelRole.White);

            return (Clamp(Read(DmxChannelRole.Red) + white), Clamp(Read(DmxChannelRole.Green) + white), Clamp(Read(DmxChannelRole.Blue) + white));
        }

        return ((byte)(255 - Read(DmxChannelRole.Cyan)), (byte)(255 - Read(DmxChannelRole.Magenta)), (byte)(255 - Read(DmxChannelRole.Yellow)));
    }

    /// <summary>
    /// The writes that make the fixture this colour. RGB fixtures with a white emitter put the
    /// colour's grey part on white (brighter, cleaner whites); CMY fixtures subtract. Brightness is
    /// left to the Intensity channel.
    /// </summary>
    public static List<DmxChannel> SetColour(DmxFixture fixture, byte red, byte green, byte blue)
    {
        List<DmxChannel> writes = [];

        void Write(DmxChannelRole role, int value)
        {
            if (AddressOf(fixture, role) is int address)
            {
                writes.Add(new DmxChannel { Address = address, Value = Clamp(value) });
            }
        }

        if (Has(fixture, DmxChannelRole.Red) || Has(fixture, DmxChannelRole.Green) || Has(fixture, DmxChannelRole.Blue))
        {
            int white = Has(fixture, DmxChannelRole.White) ? Math.Min(red, Math.Min(green, blue)) : 0;

            Write(DmxChannelRole.Red, red - white);
            Write(DmxChannelRole.Green, green - white);
            Write(DmxChannelRole.Blue, blue - white);
            Write(DmxChannelRole.White, white);
        }
        else
        {
            Write(DmxChannelRole.Cyan, 255 - red);
            Write(DmxChannelRole.Magenta, 255 - green);
            Write(DmxChannelRole.Yellow, 255 - blue);
        }

        return writes;
    }

    public static string ToHex((byte R, byte G, byte B) colour) => $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";

    public static bool TryParseHex(string? hex, out (byte R, byte G, byte B) colour)
    {
        colour = default;

        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return false;
        }

        try
        {
            colour = (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Every address the fixture occupies, 1-512 only.</summary>
    public static IEnumerable<int> Addresses(DmxFixture fixture)
    {
        int first = Math.Max(fixture.StartChannel, 1);
        int last = Math.Min(fixture.StartChannel + (fixture.FixtureMode?.ChannelCount ?? 1) - 1, UniverseSize);

        return first <= last ? Enumerable.Range(first, last - first + 1) : [];
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
