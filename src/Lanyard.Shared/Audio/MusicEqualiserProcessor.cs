namespace Lanyard.Shared.Audio;

/// <summary>
/// Runs <see cref="MusicEqualiserSettings"/> over interleaved float samples: a bass shelf, a mid
/// bell and a treble shelf per channel, then a little headroom so a boost doesn't clip.
/// </summary>
/// <remarks>
/// Built per song, because the sample rate and channel count change from track to track. Settings
/// can change mid-song with <see cref="Update"/>; the filters keep their state so there's no click.
/// </remarks>
public sealed class MusicEqualiserProcessor
{
    private readonly int _sampleRate;
    private readonly BiQuadFilter[] _bass;
    private readonly BiQuadFilter[] _mid;
    private readonly BiQuadFilter[] _treble;

    private MusicEqualiserSettings _settings = MusicEqualiserSettings.Flat;
    private float _headroom = 1f;

    public MusicEqualiserProcessor(int sampleRate, int channels, MusicEqualiserSettings settings)
    {
        _sampleRate = sampleRate;
        int count = Math.Max(1, channels);

        _bass = new BiQuadFilter[count];
        _mid = new BiQuadFilter[count];
        _treble = new BiQuadFilter[count];

        for (int i = 0; i < count; i++)
        {
            _bass[i] = new BiQuadFilter();
            _mid[i] = new BiQuadFilter();
            _treble[i] = new BiQuadFilter();
        }

        Update(settings);
    }

    public MusicEqualiserSettings Settings => _settings;

    /// <summary>
    /// The level the output is turned down by, so boosting a band doesn't push loud music past
    /// full scale: half the biggest boost, in dB. Cuts need none.
    /// </summary>
    public static float HeadroomFor(MusicEqualiserSettings settings)
    {
        int biggestBoost = Math.Max(0, Math.Max(settings.BassDb, Math.Max(settings.MidDb, settings.TrebleDb)));
        return (float)Math.Pow(10, -biggestBoost / 2.0 / 20);
    }

    public void Update(MusicEqualiserSettings settings)
    {
        MusicEqualiserSettings clamped = settings.Clamp();

        foreach (BiQuadFilter filter in _bass)
        {
            filter.SetLowShelf(_sampleRate, MusicEqualiserSettings.BassHz, clamped.BassDb);
        }

        foreach (BiQuadFilter filter in _mid)
        {
            filter.SetPeaking(_sampleRate, MusicEqualiserSettings.MidHz, MusicEqualiserSettings.MidQ, clamped.MidDb);
        }

        foreach (BiQuadFilter filter in _treble)
        {
            filter.SetHighShelf(_sampleRate, MusicEqualiserSettings.TrebleHz, clamped.TrebleDb);
        }

        _headroom = HeadroomFor(clamped);
        _settings = clamped;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        // Flat is the default for every kiosk, so leave the samples exactly as they were.
        if (_settings.IsFlat)
        {
            return;
        }

        int channels = _bass.Length;

        for (int i = 0; i < count; i++)
        {
            int channel = i % channels;
            float sample = buffer[offset + i];

            sample = _bass[channel].Process(sample);
            sample = _mid[channel].Process(sample);
            sample = _treble[channel].Process(sample);

            buffer[offset + i] = sample * _headroom;
        }
    }
}
