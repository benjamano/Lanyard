using Lanyard.Application.Services.AudioClips;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Speech;

[TestClass]
public class SpeechAudioTests
{
    private static float[] Tone(double frequency, int sampleRate, double seconds, float amplitude = 0.5f) =>
        Enumerable.Range(0, (int)(sampleRate * seconds)).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate)).ToArray();

    // Counts upward zero crossings, which for a pure tone is its frequency times its length.
    private static double FrequencyOf(float[] samples, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                crossings++;
            }
        }

        return crossings / (samples.Length / (double)sampleRate);
    }

    [TestMethod]
    public void PitchFactor_IsTwiceAsLongAnOctaveDown_AndHalfAsLongAnOctaveUp()
    {
        Assert.AreEqual(2.0, SpeechAudio.PitchFactor(-12), 1e-9);
        Assert.AreEqual(0.5, SpeechAudio.PitchFactor(12), 1e-9);
        Assert.AreEqual(1.0, SpeechAudio.PitchFactor(0), 1e-9);
    }

    [TestMethod]
    public void Stretch_ChangesTheLengthByTheFactor_AndThePitchTheOtherWay()
    {
        float[] tone = Tone(200, 16000, 1);

        float[] longer = SpeechAudio.Stretch(tone, SpeechAudio.PitchFactor(-12));

        Assert.AreEqual(tone.Length * 2, longer.Length, 1);
        Assert.AreEqual(100, FrequencyOf(longer, 16000), 3);
    }

    [TestMethod]
    public void Stretch_ByOne_ReturnsTheSameSamples()
    {
        float[] tone = Tone(200, 16000, 0.1);

        Assert.AreSame(tone, SpeechAudio.Stretch(tone, 1));
        Assert.AreEqual(0, SpeechAudio.Stretch([], 2).Length);
    }

    [TestMethod]
    public void Normalise_BringsThePeakToMinusOneDecibel_AndLeavesSilenceAlone()
    {
        float[] quiet = Tone(200, 16000, 0.1, amplitude: 0.1f);
        float[] silence = new float[100];

        SpeechAudio.Normalise(quiet);
        SpeechAudio.Normalise(silence);

        Assert.AreEqual(SpeechAudio.TargetPeak, quiet.Max(Math.Abs), 1e-3);
        Assert.IsTrue(silence.All(x => x == 0));
    }

    [TestMethod]
    public void Pad_AddsSilenceAtBothEnds()
    {
        float[] padded = SpeechAudio.Pad([1f, 1f], 1000, 0.01, 0.02);

        Assert.AreEqual(10 + 2 + 20, padded.Length);
        Assert.AreEqual(1f, padded[10]);
        Assert.AreEqual(0f, padded[0]);
        Assert.AreEqual(0f, padded[^1]);
    }

    [TestMethod]
    public void ToWav_IsAFileTheClipCheckAccepts_WithTheRightLength()
    {
        byte[] wav = SpeechAudio.ToWav(Tone(200, 22050, 2.5), 22050);

        Result<double> inspected = AudioClipFileCheck.Inspect("announcement.wav", wav.LongLength, () => new MemoryStream(wav));

        Assert.IsTrue(inspected.IsSuccess, inspected.Error);
        Assert.AreEqual(2.5, inspected.Data, 0.1);
        Assert.AreEqual(44 + (int)(22050 * 2.5) * 2, wav.Length);
    }

    [TestMethod]
    public void ToWav_ClampsSamplesOutsideTheRange()
    {
        byte[] wav = SpeechAudio.ToWav([2f, -2f], 8000);

        Assert.AreEqual(short.MaxValue, BitConverter.ToInt16(wav, 44));
        Assert.AreEqual(-short.MaxValue, BitConverter.ToInt16(wav, 46));
    }
}
