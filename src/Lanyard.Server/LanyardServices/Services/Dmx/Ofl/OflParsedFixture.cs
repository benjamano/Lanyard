using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.Application.Services.Dmx.Ofl;

// An Open Fixture Library fixture reduced to what Lanyard uses: each mode's footprint and what
// every channel in it does. Not saved; DmxFixtureDefinitionService turns it into a definition.
public sealed record OflParsedFixture(
    string Key,
    string Manufacturer,
    string Model,
    string? Revision,
    IReadOnlyList<OflParsedMode> Modes);

// Unsupported modes (pixel/matrix layouts in v1) are kept so the picker can show them greyed out
// with the reason, rather than silently missing.
public sealed record OflParsedMode(
    string Name,
    string? ShortName,
    int ChannelCount,
    bool IsSupported,
    string? UnsupportedReason,
    IReadOnlyList<OflParsedChannel> Channels);

public sealed record OflParsedChannel(
    int Offset,
    string Label,
    DmxChannelRole Role,
    int? CoarseOffset,
    byte FineLevel,
    byte DefaultValue,
    IReadOnlyList<OflParsedColorRange> ColorRanges);

public sealed record OflParsedColorRange(byte DmxStart, byte DmxEnd, string ColorHex);
