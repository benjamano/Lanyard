namespace Lanyard.Infrastructure.Models.Dmx;

// What one channel of a mode does, at a 0-based offset from the fixture's StartChannel.
public class DmxFixtureModeChannel
{
    public Guid Id { get; set; }

    public Guid ModeId { get; set; }
    public DmxFixtureMode? Mode { get; set; }

    public int Offset { get; set; }

    // The channel's name in its definition, e.g. "Red" or "Pan fine".
    public string Label { get; set; } = string.Empty;

    public DmxChannelRole Role { get; set; }

    // Set on fine (16/24-bit) channels: the offset of the coarse channel they refine, and how
    // fine they are (1 = second byte, 2 = third byte). Role matches the coarse channel's.
    public int? CoarseOffset { get; set; }
    public byte FineLevel { get; set; }

    public byte DefaultValue { get; set; }

    // Only for ColorWheel / ColorMacro channels: which DMX ranges show which colour.
    public ICollection<DmxFixtureModeChannelColorRange> ColorRanges { get; set; } = [];
}
