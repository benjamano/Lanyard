using Lanyard.Shared.AudioClips;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Lets an audio clip lower the music while it plays and bring it back afterwards.
/// </summary>
public interface IMusicDucker
{
    bool IsDucked { get; }

    void Duck(float level, TimeSpan fade);

    void Restore(TimeSpan fade);
}

/// <summary>
/// The music's loudness: the volume the server sets (0-100) times the duck level while a clip
/// plays. Applied to the samples by <see cref="GainSampleProvider"/>, not by the output device.
/// </summary>
/// <remarks>
/// WaveOutEvent.Volume goes through waveOutSetVolume, which on Windows Vista and later can set the
/// whole process's audio session, and that would lower the clip along with the music. Keeping both
/// volumes on the samples leaves the clip player's level alone whatever the music is doing.
///
/// The duck never touches the volume the server knows about, so the music schedule's volume ramps
/// and the Music page's slider carry on as if no clip were playing.
/// </remarks>
public sealed class MusicGain : IMusicDucker
{
    private readonly ILogger<MusicGain> _logger;
    private readonly DuckEnvelope _envelope = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _volumeFile;

    private int _baseVolume = 100;

    public MusicGain(ILogger<MusicGain> logger)
    {
        _logger = logger;

        // Remembered across restarts; the device used to keep it, now nothing else would.
        _volumeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lanyardClient", "music-volume.txt");
        _baseVolume = LoadVolume();
    }

    public int BaseVolume => Volatile.Read(ref _baseVolume);

    public bool IsDucked => _envelope.IsDucked;

    public TimeSpan Now => _clock.Elapsed;

    public void SetBaseVolume(int volume)
    {
        int clamped = Math.Clamp(volume, 0, 100);
        Volatile.Write(ref _baseVolume, clamped);
        SaveVolume(clamped);
    }

    /// <summary>The factor to multiply music samples by at this moment.</summary>
    public float GainAt(TimeSpan now) => BaseVolume / 100f * _envelope.GainAt(now);

    public void Duck(float level, TimeSpan fade)
    {
        _logger.LogInformation("MusicGain: Ducking music to {Percent}% over {FadeMs}ms", Math.Round(level * 100), fade.TotalMilliseconds);
        _envelope.DuckTo(level, fade, Now);
    }

    public void Restore(TimeSpan fade)
    {
        _logger.LogInformation("MusicGain: Restoring music over {FadeMs}ms", fade.TotalMilliseconds);
        _envelope.Restore(fade, Now);
    }

    private int LoadVolume()
    {
        try
        {
            if (File.Exists(_volumeFile) && int.TryParse(File.ReadAllText(_volumeFile).Trim(), out int saved))
            {
                return Math.Clamp(saved, 0, 100);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicGain: Failed to read the saved music volume, using 100");
        }

        return 100;
    }

    private void SaveVolume(int volume)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_volumeFile)!);
            File.WriteAllText(_volumeFile, volume.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicGain: Failed to save the music volume");
        }
    }
}
