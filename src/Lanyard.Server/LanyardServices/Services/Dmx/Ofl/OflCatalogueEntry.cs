namespace Lanyard.Application.Services.Dmx.Ofl;

// One search result: enough to show "Cameo Flat Pro 18 - 5 modes" without parsing it again.
public sealed record OflCatalogueEntry(string Key, string Manufacturer, string Model, IReadOnlyList<OflCatalogueModeSummary> Modes)
{
    public string SearchText => $"{Manufacturer} {Model} {Key}";
}

public sealed record OflCatalogueModeSummary(string Name, int ChannelCount, bool IsSupported);

public sealed record OflSnapshotInfo(string Commit, DateTime? BuiltAt, int FixtureCount);
