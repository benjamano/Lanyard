using NAudio.Wave;

/// <summary>
/// Applies <see cref="MusicGain"/> to the music's samples. The gain is worked out at the start and
/// end of each buffer and blended across it, so a duck fades smoothly even though WaveOutEvent
/// asks for audio in chunks of a hundred milliseconds or more.
/// </summary>
public sealed class GainSampleProvider(ISampleProvider source, MusicGain gain) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);

        if (read <= 0)
        {
            return read;
        }

        int channels = Math.Max(1, WaveFormat.Channels);
        int frames = read / channels;
        TimeSpan start = gain.Now;
        TimeSpan length = TimeSpan.FromSeconds((double)frames / WaveFormat.SampleRate);

        float from = gain.GainAt(start);
        float to = gain.GainAt(start + length);

        for (int frame = 0; frame < frames; frame++)
        {
            float factor = frames > 1 ? from + (to - from) * frame / (frames - 1) : to;
            int index = offset + frame * channels;

            for (int channel = 0; channel < channels; channel++)
            {
                buffer[index + channel] *= factor;
            }
        }

        // Any samples left over from a partial frame still get the closing level.
        for (int i = offset + frames * channels; i < offset + read; i++)
        {
            buffer[i] *= to;
        }

        return read;
    }
}
