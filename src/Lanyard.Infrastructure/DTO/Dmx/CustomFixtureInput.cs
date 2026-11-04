using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.Infrastructure.DTO.Dmx;

// A fixture type built by hand for a light the Open Fixture Library doesn't have.
public record CustomFixtureInput(string Manufacturer, string Model, IReadOnlyList<CustomFixtureModeInput> Modes);

public record CustomFixtureModeInput(string Name, IReadOnlyList<CustomFixtureChannelInput> Channels);

// IsFine: a 16-bit fine channel for the nearest channel above it that isn't fine itself (it takes
// that channel's role). ColorRanges only apply to colour wheel, colour macro and strobe channels.
public record CustomFixtureChannelInput(string Label, DmxChannelRole Role, bool IsFine, IReadOnlyList<CustomFixtureColorRangeInput> ColorRanges);

public record CustomFixtureColorRangeInput(byte DmxStart, byte DmxEnd, string ColorHex);
