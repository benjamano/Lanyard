using Lanyard.Application.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Music;

[TestClass]
public class SongBpmAnalyzerTests
{
    private const int SampleRate = 44100;

    /// <summary>
    /// Builds a mono click track: short noise bursts at the given tempo, starting
    /// at <paramref name="firstBeatOffsetSeconds"/>, over otherwise silent audio.
    /// </summary>
    private static float[] GenerateClickTrack(double bpm, double firstBeatOffsetSeconds, double durationSeconds)
    {
        int totalSamples = (int)(durationSeconds * SampleRate);
        float[] samples = new float[totalSamples];

        double beatLengthSeconds = 60.0 / bpm;
        int clickLengthSamples = SampleRate / 100; // 10ms click
        Random random = new(42);

        for (double beatTime = firstBeatOffsetSeconds; beatTime < durationSeconds; beatTime += beatLengthSeconds)
        {
            int start = (int)(beatTime * SampleRate);

            for (int i = 0; i < clickLengthSamples && start + i < totalSamples; i++)
            {
                // Decaying noise burst — a percussive attack with a sharp energy rise.
                double decay = 1.0 - ((double)i / clickLengthSamples);
                samples[start + i] = (float)(((random.NextDouble() * 2) - 1) * decay * 0.9);
            }
        }

        return samples;
    }

    private static void AssertOffsetMatches(double expectedOffset, double actualOffset, double beatLengthSeconds, double toleranceSeconds)
    {
        // Offsets are equivalent modulo the beat length; compare on the circle.
        double difference = Math.Abs(expectedOffset - actualOffset) % beatLengthSeconds;
        double circularDifference = Math.Min(difference, beatLengthSeconds - difference);

        Assert.IsLessThanOrEqualTo(toleranceSeconds, circularDifference,
            $"Beat offset {actualOffset:F4}s not within {toleranceSeconds * 1000}ms of expected {expectedOffset:F4}s (beat length {beatLengthSeconds:F4}s)");
    }

    [TestMethod]
    public void Analyze_120BpmClickTrack_DetectsTempoAndPhase()
    {
        float[] samples = GenerateClickTrack(120, 0.25, 30);

        BpmAnalysisResult? result = SongBpmAnalyzer.Analyze(samples, SampleRate);

        Assert.IsNotNull(result);
        Assert.AreEqual(120, result.Bpm, 1.0, $"Detected {result.Bpm:F2} BPM");
        AssertOffsetMatches(0.25, result.FirstBeatOffsetSeconds, 60.0 / result.Bpm, 0.03);
    }

    [TestMethod]
    public void Analyze_90BpmClickTrack_DetectsTempo()
    {
        float[] samples = GenerateClickTrack(90, 0.0, 30);

        BpmAnalysisResult? result = SongBpmAnalyzer.Analyze(samples, SampleRate);

        Assert.IsNotNull(result);
        Assert.AreEqual(90, result.Bpm, 1.0, $"Detected {result.Bpm:F2} BPM");
    }

    [TestMethod]
    public void Analyze_WithKnownBpm_EstimatesPhaseOnly()
    {
        float[] samples = GenerateClickTrack(120, 0.4, 30);

        BpmAnalysisResult? result = SongBpmAnalyzer.Analyze(samples, SampleRate, knownBpm: 120);

        Assert.IsNotNull(result);
        Assert.AreEqual(120, result.Bpm, 1e-9); // Tag BPM passed straight through.
        AssertOffsetMatches(0.4, result.FirstBeatOffsetSeconds, 0.5, 0.03);
    }

    [TestMethod]
    public void Analyze_Silence_ReturnsNull()
    {
        float[] samples = new float[SampleRate * 30];

        BpmAnalysisResult? result = SongBpmAnalyzer.Analyze(samples, SampleRate);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Analyze_TooShort_ReturnsNull()
    {
        float[] samples = GenerateClickTrack(120, 0, 0.05);

        BpmAnalysisResult? result = SongBpmAnalyzer.Analyze(samples, SampleRate);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Analyze_InvalidInputs_ReturnsNull()
    {
        Assert.IsNull(SongBpmAnalyzer.Analyze(null!, SampleRate));
        Assert.IsNull(SongBpmAnalyzer.Analyze(new float[SampleRate], 0));
    }
}
