using Lanyard.Shared.Audio;
using NAudio.Wave;

/// <summary>
/// Applies the kiosk's <see cref="MusicEqualiser"/> to the music's samples. One is made per song,
/// for that song's sample rate and channels, and it picks up setting changes between buffers.
/// </summary>
public sealed class EqualiserSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly MusicEqualiser _equaliser;
    private readonly MusicEqualiserProcessor _processor;
    private int _appliedVersion;

    public EqualiserSampleProvider(ISampleProvider source, MusicEqualiser equaliser)
    {
        _source = source;
        _equaliser = equaliser;
        _appliedVersion = equaliser.Version;
        _processor = new MusicEqualiserProcessor(source.WaveFormat.SampleRate, source.WaveFormat.Channels, equaliser.Settings);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        if (read <= 0)
        {
            return read;
        }

        int version = _equaliser.Version;

        if (version != _appliedVersion)
        {
            _appliedVersion = version;
            _processor.Update(_equaliser.Settings);
        }

        _processor.Process(buffer, offset, read);
        return read;
    }
}
