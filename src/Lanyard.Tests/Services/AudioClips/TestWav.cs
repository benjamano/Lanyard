using System.Text;

namespace Lanyard.Tests.Services.AudioClips;

// A real (silent) WAV file of any length, small enough to build in memory: 8 kHz, mono, 8-bit.
internal static class TestWav
{
    public static byte[] OfSeconds(double seconds)
    {
        const int sampleRate = 8000;
        int dataLength = (int)(sampleRate * seconds);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(Enumerable.Repeat((byte)128, dataLength).ToArray());
        writer.Flush();

        return stream.ToArray();
    }
}
