using System.Text;

namespace Lanyard.Shared.Audio;

/// <summary>
/// The sample maths a spoken announcement goes through after the speech engine and before it's
/// stored: pitch, loudness and the WAV file itself. Kept free of NAudio, like BiQuadFilter, so it
/// can be unit tested and run on the Linux server.
/// </summary>
public static class SpeechAudio
{
    // −1 dBFS: as loud as it can be without the kiosk's own resampling clipping the peaks.
    public const float TargetPeak = 0.891f;

    /// <summary>
    /// Changes the length of the audio by <paramref name="factor"/> (2 = twice as long) with
    /// linear interpolation. Played back at the original rate, stretching lowers the pitch and
    /// squashing raises it. Paired with telling the engine to speak faster or slower by the same
    /// factor, this moves the pitch while keeping the length.
    /// </summary>
    public static float[] Stretch(float[] samples, double factor)
    {
        if (samples.Length == 0 || factor <= 0 || Math.Abs(factor - 1) < 1e-6)
        {
            return samples;
        }

        int length = Math.Max(1, (int)Math.Round(samples.Length * factor));
        float[] stretched = new float[length];

        for (int i = 0; i < length; i++)
        {
            double source = i / factor;
            int index = (int)source;

            if (index >= samples.Length - 1)
            {
                stretched[i] = samples[^1];
                continue;
            }

            double fraction = source - index;
            stretched[i] = (float)(samples[index] + ((samples[index + 1] - samples[index]) * fraction));
        }

        return stretched;
    }

    /// <summary>The length factor for a pitch change in semitones: down 12 is twice as long.</summary>
    public static double PitchFactor(double semitones) => Math.Pow(2, -semitones / 12.0);

    /// <summary>
    /// Scales the audio so its loudest sample sits at <see cref="TargetPeak"/>. Silence stays
    /// silence. Clips arrive at the speakers the same loudness whichever voice spoke them; the
    /// clip's own volume setting still applies on the kiosk.
    /// </summary>
    public static void Normalise(float[] samples)
    {
        float peak = 0;

        foreach (float sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak < 1e-4f)
        {
            return;
        }

        float gain = TargetPeak / peak;

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    /// <summary>Adds silence at both ends so the first word isn't lost while the music dips.</summary>
    public static float[] Pad(float[] samples, int sampleRate, double leadSeconds, double tailSeconds)
    {
        int lead = (int)(sampleRate * Math.Max(0, leadSeconds));
        int tail = (int)(sampleRate * Math.Max(0, tailSeconds));
        float[] padded = new float[lead + samples.Length + tail];
        Array.Copy(samples, 0, padded, lead, samples.Length);
        return padded;
    }

    /// <summary>A mono 16-bit PCM WAV file, which every kiosk and browser can play.</summary>
    public static byte[] ToWav(float[] samples, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int dataLength = samples.Length * 2;

        using MemoryStream stream = new(44 + dataLength);
        using BinaryWriter writer = new(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (float sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
