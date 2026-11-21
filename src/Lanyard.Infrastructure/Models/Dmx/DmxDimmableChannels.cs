namespace Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// Which DMX addresses the grand master and blackout act on. Pure, so the engine and tests share it.
/// </summary>
/// <remarks>
/// A grand master that scaled every channel would swing moving heads (pan/tilt), change gobos and
/// fire strobes as it faded. So only brightness is touched:
/// <list type="bullet">
/// <item>a fixture's Intensity channels;</item>
/// <item>a fixture with no Intensity channel: its additive emitters (red, green, white...), which are
/// how its brightness is set;</item>
/// <item>unpatched channels, which on a desk without fixtures are almost always dimmer packs.</item>
/// </list>
/// Everything else a fixture has (position, colour wheel, gobo, strobe, CMY...) is left alone. A
/// channel two fixtures share is only dimmable if both agree, so a double patch never scales a pan.
/// </remarks>
public static class DmxDimmableChannels
{
    public const int UniverseSize = 512;

    public static bool IsEmitter(DmxChannelRole role) => role is >= DmxChannelRole.Red and <= DmxChannelRole.Indigo;

    /// <summary>Index = address (1-512); index 0 is unused.</summary>
    public static bool[] Build(IEnumerable<DmxFixture> fixtures)
    {
        bool[] dimmable = new bool[UniverseSize + 1];
        Array.Fill(dimmable, true, 1, UniverseSize);

        foreach (DmxFixture fixture in fixtures)
        {
            IReadOnlyCollection<DmxFixtureModeChannel> channels = fixture.FixtureMode?.Channels?.ToList() ?? [];
            bool hasIntensity = channels.Any(c => c.Role == DmxChannelRole.Intensity);

            foreach (DmxFixtureModeChannel channel in channels)
            {
                int address = fixture.StartChannel + channel.Offset;

                if (address is < 1 or > UniverseSize)
                {
                    continue;
                }

                bool scales = channel.Role == DmxChannelRole.Intensity || (!hasIntensity && IsEmitter(channel.Role));

                dimmable[address] &= scales;
            }
        }

        return dimmable;
    }

    /// <summary>The level that goes out on a channel once master and blackout are applied.</summary>
    public static byte Output(byte programmed, bool isDimmable, byte grandMaster, bool isBlackout)
    {
        if (!isDimmable)
        {
            return programmed;
        }

        if (isBlackout)
        {
            return 0;
        }

        // Rounded, so a full master is exactly the programmed value and 1 at full stays 1.
        return (byte)((programmed * grandMaster + 127) / 255);
    }
}
