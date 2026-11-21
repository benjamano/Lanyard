using System.Text.Json.Serialization;

namespace Lanyard.Shared.Audio;

/// <summary>
/// A kiosk's three-band music equaliser, in whole decibels. Zero is flat. Sent to the kiosk with
/// <c>ClientHubMethods.ReceiveEqualiserSettings</c> and stored on the server's Client row.
/// </summary>
/// <remarks>Only the music goes through it. Audio clips play on their own output and stay unaltered.</remarks>
public sealed record MusicEqualiserSettings(int BassDb, int MidDb, int TrebleDb)
{
    public const int MinDb = -12;
    public const int MaxDb = 12;

    // Older kiosks have no handler for ReceiveEqualiserSettings and drop it without an error, so
    // the server checks this to tell staff the kiosk needs updating.
    public const string MinimumClientVersion = "1.0.48";

    // Where each band sits. Bass and treble are shelves, so "bass up" lifts everything below 100 Hz.
    public const double BassHz = 100;
    public const double MidHz = 1000;
    public const double MidQ = 0.9;
    public const double TrebleHz = 8000;

    public static MusicEqualiserSettings Flat { get; } = new(0, 0, 0);

    /// <summary>Whether a kiosk reporting this version applies the equaliser. Null (never reported) is too old.</summary>
    public static bool IsSupportedBy(string? reportedClientVersion) =>
        Lanyard.Shared.Contracts.ClientVersionFormat.TryNormalize(reportedClientVersion, out string version)
        && Lanyard.Shared.Contracts.ClientVersionFormat.Compare(version, MinimumClientVersion) >= 0;

    [JsonIgnore]
    public bool IsFlat => BassDb == 0 && MidDb == 0 && TrebleDb == 0;

    public MusicEqualiserSettings Clamp() => new(ClampDb(BassDb), ClampDb(MidDb), ClampDb(TrebleDb));

    public static int ClampDb(int db) => Math.Clamp(db, MinDb, MaxDb);

    /// <summary>The built-in presets, in the order the UI shows them.</summary>
    public static IReadOnlyList<MusicEqualiserPreset> Presets { get; } =
    [
        new("Flat", Flat),
        new("Bass Boost", new(6, 0, 1)),
        new("Vocal", new(-2, 4, 2)),
        new("Treble Boost", new(0, 0, 6)),
        new("Warm", new(3, 1, -2)),
    ];

    /// <summary>The preset these settings match exactly, or null for a custom setting.</summary>
    [JsonIgnore]
    public MusicEqualiserPreset? MatchingPreset => Presets.FirstOrDefault(p => p.Settings == this);
}

public sealed record MusicEqualiserPreset(string Name, MusicEqualiserSettings Settings);
