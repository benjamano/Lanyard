namespace Lanyard.Infrastructure.Models.Dmx;

// The three generic fixtures Lanyard had before fixture definitions existed (the old
// DmxFixtureType enum). Seeded by ApplicationDbContext.HasData with fixed ids so the migration can
// point every existing fixture at the matching mode; their channel layout must never change, or
// existing fixtures would start rendering differently.
public static class BuiltInDmxFixtureDefinitions
{
    public const string Manufacturer = "Generic";

    // Declared before Definitions: static fields initialise in source order.
    private static readonly DateTime SeedDate = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    public static readonly Guid DimmerDefinitionId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000001");
    public static readonly Guid RgbDefinitionId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000002");
    public static readonly Guid RgbDimmerDefinitionId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000003");

    public static readonly Guid DimmerModeId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000101");
    public static readonly Guid RgbModeId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000102");
    public static readonly Guid RgbDimmerModeId = new("6f1d6a8e-1c1b-4c55-9a0e-000000000103");

    public static Guid ModeIdFor(DmxFixtureType fixtureType) => fixtureType switch
    {
        DmxFixtureType.Dimmer => DimmerModeId,
        DmxFixtureType.Rgb => RgbModeId,
        DmxFixtureType.RgbDimmer => RgbDimmerModeId,
        _ => throw new ArgumentOutOfRangeException(nameof(fixtureType), fixtureType, "Unknown fixture type.")
    };

    // The old enum value for a built-in mode, kept in DmxFixture.FixtureType so an app rollback
    // still renders these fixtures. Null for any other mode.
    public static DmxFixtureType? FixtureTypeFor(Guid modeId)
    {
        if (modeId == DimmerModeId)
        {
            return DmxFixtureType.Dimmer;
        }

        if (modeId == RgbModeId)
        {
            return DmxFixtureType.Rgb;
        }

        if (modeId == RgbDimmerModeId)
        {
            return DmxFixtureType.RgbDimmer;
        }

        return null;
    }

    public static IReadOnlyList<DmxFixtureDefinition> Definitions { get; } =
    [
        new DmxFixtureDefinition { Id = DimmerDefinitionId, Source = DmxFixtureDefinitionSource.BuiltIn, Manufacturer = Manufacturer, Model = "Dimmer", IsActive = true, CreateDate = SeedDate },
        new DmxFixtureDefinition { Id = RgbDefinitionId, Source = DmxFixtureDefinitionSource.BuiltIn, Manufacturer = Manufacturer, Model = "RGB", IsActive = true, CreateDate = SeedDate },
        new DmxFixtureDefinition { Id = RgbDimmerDefinitionId, Source = DmxFixtureDefinitionSource.BuiltIn, Manufacturer = Manufacturer, Model = "RGB + Dimmer", IsActive = true, CreateDate = SeedDate },
    ];

    public static IReadOnlyList<DmxFixtureMode> Modes { get; } =
    [
        new DmxFixtureMode { Id = DimmerModeId, DefinitionId = DimmerDefinitionId, Name = "1-channel", ShortName = "1ch", ChannelCount = 1, SortOrder = 0 },
        new DmxFixtureMode { Id = RgbModeId, DefinitionId = RgbDefinitionId, Name = "3-channel", ShortName = "3ch", ChannelCount = 3, SortOrder = 0 },
        new DmxFixtureMode { Id = RgbDimmerModeId, DefinitionId = RgbDimmerDefinitionId, Name = "4-channel", ShortName = "4ch", ChannelCount = 4, SortOrder = 0 },
    ];

    public static IReadOnlyList<DmxFixtureModeChannel> Channels { get; } =
    [
        Channel(1, DimmerModeId, 0, "Dimmer", DmxChannelRole.Intensity),

        Channel(2, RgbModeId, 0, "Red", DmxChannelRole.Red),
        Channel(3, RgbModeId, 1, "Green", DmxChannelRole.Green),
        Channel(4, RgbModeId, 2, "Blue", DmxChannelRole.Blue),

        Channel(5, RgbDimmerModeId, 0, "Red", DmxChannelRole.Red),
        Channel(6, RgbDimmerModeId, 1, "Green", DmxChannelRole.Green),
        Channel(7, RgbDimmerModeId, 2, "Blue", DmxChannelRole.Blue),
        Channel(8, RgbDimmerModeId, 3, "Dimmer", DmxChannelRole.Intensity),
    ];

    // A complete in-memory mode (with channels) for callers that need one without a database,
    // e.g. tests and the colour mapper's fallback for fixtures loaded without their mode.
    public static DmxFixtureMode BuildMode(Guid modeId)
    {
        DmxFixtureMode source = Modes.Single(m => m.Id == modeId);

        return new DmxFixtureMode
        {
            Id = source.Id,
            DefinitionId = source.DefinitionId,
            Name = source.Name,
            ShortName = source.ShortName,
            ChannelCount = source.ChannelCount,
            SortOrder = source.SortOrder,
            Channels = Channels
                .Where(c => c.ModeId == modeId)
                .Select(c => new DmxFixtureModeChannel { Id = c.Id, ModeId = c.ModeId, Offset = c.Offset, Label = c.Label, Role = c.Role })
                .ToList()
        };
    }

    private static DmxFixtureModeChannel Channel(int index, Guid modeId, int offset, string label, DmxChannelRole role) => new()
    {
        Id = new Guid($"6f1d6a8e-1c1b-4c55-9a0e-000000001{index:D3}"),
        ModeId = modeId,
        Offset = offset,
        Label = label,
        Role = role
    };
}
