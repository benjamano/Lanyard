namespace Lanyard.Infrastructure.Models.Dmx;

public enum DmxFixtureType
{
    Dimmer,     // offset 0: Dimmer
    Rgb,        // offsets 0-2: Red, Green, Blue
    RgbDimmer   // offsets 0-3: Red, Green, Blue, Dimmer
}
