using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.App.Components.Dmx;

// How channel roles read in the UI: a full name for pickers, and a tag short enough to sit under a
// 40px-wide desk slider.
public static class DmxChannelRoleText
{
    public static string Name(DmxChannelRole role) => role switch
    {
        DmxChannelRole.Generic => "Other",
        DmxChannelRole.NoFunction => "Unused",
        DmxChannelRole.Intensity => "Dimmer",
        DmxChannelRole.WarmWhite => "Warm white",
        DmxChannelRole.ColdWhite => "Cold white",
        DmxChannelRole.Uv => "UV",
        DmxChannelRole.ColorTemperature => "Colour temperature",
        DmxChannelRole.ColorWheel => "Colour wheel",
        DmxChannelRole.ColorMacro => "Colour macro",
        DmxChannelRole.Strobe => "Strobe / shutter",
        DmxChannelRole.PanTiltSpeed => "Pan/tilt speed",
        _ => role.ToString(),
    };

    public static string Tag(DmxChannelRole role) => role switch
    {
        DmxChannelRole.Generic => "Ch",
        DmxChannelRole.NoFunction => "-",
        DmxChannelRole.Intensity => "Dim",
        DmxChannelRole.Red => "R",
        DmxChannelRole.Green => "G",
        DmxChannelRole.Blue => "B",
        DmxChannelRole.White => "W",
        DmxChannelRole.WarmWhite => "WW",
        DmxChannelRole.ColdWhite => "CW",
        DmxChannelRole.Amber => "A",
        DmxChannelRole.Lime => "L",
        DmxChannelRole.Uv => "UV",
        DmxChannelRole.Indigo => "I",
        DmxChannelRole.Cyan => "C",
        DmxChannelRole.Magenta => "M",
        DmxChannelRole.Yellow => "Y",
        DmxChannelRole.ColorTemperature => "CTC",
        DmxChannelRole.ColorWheel => "Col",
        DmxChannelRole.ColorMacro => "Mac",
        DmxChannelRole.Strobe => "Str",
        DmxChannelRole.Pan => "Pan",
        DmxChannelRole.Tilt => "Tilt",
        DmxChannelRole.PanTiltSpeed => "PTS",
        DmxChannelRole.Gobo => "Gobo",
        DmxChannelRole.Prism => "Pri",
        DmxChannelRole.Focus => "Foc",
        DmxChannelRole.Zoom => "Zm",
        DmxChannelRole.Iris => "Iris",
        DmxChannelRole.Frost => "Fro",
        DmxChannelRole.Speed => "Spd",
        DmxChannelRole.Effect => "Fx",
        DmxChannelRole.Fog => "Fog",
        DmxChannelRole.Maintenance => "Ctl",
        _ => "Ch",
    };

    // Order the role picker reads in: the common colour channels first.
    public static IReadOnlyList<DmxChannelRole> PickerOrder { get; } =
    [
        DmxChannelRole.Intensity,
        DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue, DmxChannelRole.White,
        DmxChannelRole.WarmWhite, DmxChannelRole.ColdWhite, DmxChannelRole.Amber, DmxChannelRole.Lime,
        DmxChannelRole.Uv, DmxChannelRole.Indigo,
        DmxChannelRole.Cyan, DmxChannelRole.Magenta, DmxChannelRole.Yellow,
        DmxChannelRole.ColorTemperature, DmxChannelRole.ColorWheel, DmxChannelRole.ColorMacro,
        DmxChannelRole.Strobe, DmxChannelRole.Pan, DmxChannelRole.Tilt, DmxChannelRole.PanTiltSpeed,
        DmxChannelRole.Gobo, DmxChannelRole.Prism, DmxChannelRole.Focus, DmxChannelRole.Zoom,
        DmxChannelRole.Iris, DmxChannelRole.Frost,
        DmxChannelRole.Speed, DmxChannelRole.Effect, DmxChannelRole.Fog, DmxChannelRole.Maintenance,
        DmxChannelRole.Generic, DmxChannelRole.NoFunction,
    ];
}
