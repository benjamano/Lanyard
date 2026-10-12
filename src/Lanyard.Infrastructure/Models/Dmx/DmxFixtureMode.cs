namespace Lanyard.Infrastructure.Models.Dmx;

// One DMX personality of a fixture definition, e.g. "5-channel". ChannelCount is the fixture's
// footprint: a fixture patched at StartChannel S in this mode uses S .. S + ChannelCount - 1.
public class DmxFixtureMode
{
    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }
    public DmxFixtureDefinition? Definition { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }

    public int ChannelCount { get; set; }
    public int SortOrder { get; set; }

    public ICollection<DmxFixtureModeChannel> Channels { get; set; } = [];
}
