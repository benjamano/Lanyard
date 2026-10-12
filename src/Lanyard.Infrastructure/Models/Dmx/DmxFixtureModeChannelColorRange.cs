namespace Lanyard.Infrastructure.Models.Dmx;

// A DMX value range on a colour wheel or colour macro channel and the colour it produces.
public class DmxFixtureModeChannelColorRange
{
    public Guid Id { get; set; }

    public Guid ModeChannelId { get; set; }
    public DmxFixtureModeChannel? ModeChannel { get; set; }

    public byte DmxStart { get; set; }
    public byte DmxEnd { get; set; }

    // "#RRGGBB"
    public string ColorHex { get; set; } = string.Empty;
}
