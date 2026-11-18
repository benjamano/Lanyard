using Lanyard.Shared.Audio;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

/// <summary>
/// The kiosk's current music equaliser. The server sends it on every connect and when staff change
/// it; it's also saved locally so offline music after a restart still sounds the same.
/// </summary>
public sealed class MusicEqualiser
{
    private readonly ILogger<MusicEqualiser> _logger;
    private readonly string _settingsFile;

    private MusicEqualiserSettings _settings;
    private int _version;

    public MusicEqualiser(ILogger<MusicEqualiser> logger)
    {
        _logger = logger;
        _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lanyardClient", "music-eq.json");
        _settings = Load();
    }

    public MusicEqualiserSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Goes up every time the settings change, so the audio thread knows to update its filters.</summary>
    public int Version => Volatile.Read(ref _version);

    public void Apply(MusicEqualiserSettings settings)
    {
        MusicEqualiserSettings clamped = settings.Clamp();

        if (clamped == Settings)
        {
            return;
        }

        _logger.LogInformation("MusicEqualiser: Bass {BassDb} dB, mid {MidDb} dB, treble {TrebleDb} dB", clamped.BassDb, clamped.MidDb, clamped.TrebleDb);

        Volatile.Write(ref _settings, clamped);
        Interlocked.Increment(ref _version);
        Save(clamped);
    }

    private MusicEqualiserSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                MusicEqualiserSettings? saved = JsonSerializer.Deserialize<MusicEqualiserSettings>(File.ReadAllText(_settingsFile));

                if (saved is not null)
                {
                    return saved.Clamp();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicEqualiser: Failed to read the saved equaliser, using flat");
        }

        return MusicEqualiserSettings.Flat;
    }

    private void Save(MusicEqualiserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicEqualiser: Failed to save the equaliser");
        }
    }
}
