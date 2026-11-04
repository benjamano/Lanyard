namespace Lanyard.Infrastructure.Models.Dmx;

// What a single DMX channel of a fixture mode controls. Stored as an integer, so every value is
// pinned: never renumber or reuse one.
public enum DmxChannelRole
{
    Generic = 0,
    NoFunction = 1,
    Intensity = 2,

    // Additive colour emitters
    Red = 10,
    Green = 11,
    Blue = 12,
    White = 13,
    WarmWhite = 14,
    ColdWhite = 15,
    Amber = 16,
    Lime = 17,
    Uv = 18,
    Indigo = 19,

    // Subtractive colour mixing
    Cyan = 30,
    Magenta = 31,
    Yellow = 32,

    ColorTemperature = 40,
    ColorWheel = 41,
    ColorMacro = 42,

    Strobe = 50,
    Pan = 51,
    Tilt = 52,
    PanTiltSpeed = 53,
    Gobo = 54,
    Prism = 55,
    Focus = 56,
    Zoom = 57,
    Iris = 58,
    Frost = 59,

    Speed = 70,
    Effect = 71,
    Fog = 72,
    Maintenance = 73
}
