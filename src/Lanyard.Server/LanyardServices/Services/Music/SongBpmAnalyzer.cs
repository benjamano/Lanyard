namespace Lanyard.Application.Services;

/// <summary>
/// Result of tempo analysis: BPM, the offset of the first beat from the start of
/// the track (reduced into [0, beatLength)), and a confidence score.
/// </summary>
public sealed record BpmAnalysisResult(double Bpm, double FirstBeatOffsetSeconds, double Confidence);

/// <summary>
/// Pure-DSP tempo and beat-phase estimation over raw PCM samples. No file I/O and
/// no NAudio dependency, so it is unit-testable with synthetic buffers.
///
/// Method: an onset-strength envelope is built from half-wave-rectified frame
/// energy differences (energy flux — adequate for the beat-driven material this
/// feature targets, and needs no FFT). Tempo comes from autocorrelating that
/// envelope across the 60-200 BPM lag range with an octave-error preference for
/// 90-180 BPM; beat phase comes from folding the envelope at the beat period and
/// taking the strongest offset.
/// </summary>
public static class SongBpmAnalyzer
{
    private const int FrameSize = 1024;
    private const int HopSize = 512;

    private const double MinBpm = 60;
    private const double MaxBpm = 200;

    // Octave-error resolution prefers candidates in this "danceable" range when
    // scores are close (a 70 BPM peak and its 140 BPM double are near-equivalent
    // in autocorrelation; the double is almost always the perceived tempo).
    private const double PreferredMinBpm = 90;
    private const double PreferredMaxBpm = 180;
    private const double OctavePreferenceTolerance = 0.10;

    // Peak autocorrelation must stand this far above the mean over the search
    // range, or the material is treated as having no reliable tempo.
    private const double MinConfidence = 2.0;

    // Only the first stretch of the track is analyzed; it must start at t=0 so the
    // reported beat offset is relative to the start of the file.
    private const double MaxAnalysisSeconds = 90;

    /// <summary>
    /// Analyzes mono PCM samples. When <paramref name="knownBpm"/> is given (e.g.
    /// from a TBPM tag) tempo detection is skipped and only the beat phase is
    /// estimated. Returns null when no confident tempo can be found.
    /// </summary>
    public static BpmAnalysisResult? Analyze(float[] samples, int sampleRate, double? knownBpm = null)
    {
        if (samples is null || sampleRate <= 0)
        {
            return null;
        }

        int usableSamples = (int)Math.Min(samples.Length, (long)(MaxAnalysisSeconds * sampleRate));
        int frameCount = (usableSamples - FrameSize) / HopSize;

        if (frameCount < 8)
        {
            return null;
        }

        double[] onsetEnvelope = BuildOnsetEnvelope(samples, frameCount);

        if (onsetEnvelope.All(value => value == 0))
        {
            return null;
        }

        double framesPerSecond = (double)sampleRate / HopSize;

        double bpm;
        double confidence;

        if (knownBpm is > 0)
        {
            bpm = knownBpm.Value;
            confidence = double.MaxValue;
        }
        else
        {
            (double detectedBpm, double detectedConfidence)? tempo = DetectTempo(onsetEnvelope, framesPerSecond);

            if (tempo is null)
            {
                return null;
            }

            (bpm, confidence) = tempo.Value;
        }

        double beatLengthSeconds = 60.0 / bpm;
        double offsetSeconds = EstimateBeatPhase(onsetEnvelope, framesPerSecond, beatLengthSeconds);

        return new BpmAnalysisResult(bpm, offsetSeconds, confidence);
    }

    private static double[] BuildOnsetEnvelope(float[] samples, int frameCount)
    {
        double[] energies = new double[frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            int start = frame * HopSize;
            double sum = 0;

            for (int i = 0; i < FrameSize; i++)
            {
                double sample = samples[start + i];
                sum += sample * sample;
            }

            energies[frame] = Math.Sqrt(sum / FrameSize);
        }

        // Half-wave-rectified energy rise: silence and steady tones contribute
        // nothing; percussive attacks spike.
        double[] envelope = new double[frameCount];
        double max = 0;

        for (int frame = 1; frame < frameCount; frame++)
        {
            envelope[frame] = Math.Max(0, energies[frame] - energies[frame - 1]);
            max = Math.Max(max, envelope[frame]);
        }

        if (max > 0)
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                envelope[frame] /= max;
            }
        }

        return envelope;
    }

    private static (double bpm, double confidence)? DetectTempo(double[] envelope, double framesPerSecond)
    {
        int minLag = (int)Math.Floor(framesPerSecond * 60.0 / MaxBpm);
        int maxLag = (int)Math.Ceiling(framesPerSecond * 60.0 / MinBpm);

        if (minLag < 1 || maxLag >= envelope.Length / 2)
        {
            return null;
        }

        double[] correlation = new double[maxLag + 1];
        double total = 0;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;

            for (int i = 0; i + lag < envelope.Length; i++)
            {
                sum += envelope[i] * envelope[i + lag];
            }

            // Normalize by overlap so long lags are not penalized for having fewer terms.
            correlation[lag] = sum / (envelope.Length - lag);
            total += correlation[lag];
        }

        double mean = total / (maxLag - minLag + 1);

        if (mean <= 0)
        {
            return null;
        }

        int bestLag = minLag;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (correlation[lag] > correlation[bestLag])
            {
                bestLag = lag;
            }
        }

        // Octave-error handling: if half or double the winning lag scores nearly as
        // well and lands in the preferred BPM range while the winner does not,
        // take it instead.
        bestLag = ResolveOctave(correlation, bestLag, minLag, maxLag, framesPerSecond);

        double confidence = correlation[bestLag] / mean;

        if (confidence < MinConfidence)
        {
            return null;
        }

        double refinedLag = RefinePeak(correlation, bestLag, minLag, maxLag);
        double bpm = 60.0 * framesPerSecond / refinedLag;

        return (bpm, confidence);
    }

    private static int ResolveOctave(double[] correlation, int bestLag, int minLag, int maxLag, double framesPerSecond)
    {
        double bestBpm = 60.0 * framesPerSecond / bestLag;
        bool bestPreferred = bestBpm is >= PreferredMinBpm and <= PreferredMaxBpm;

        if (bestPreferred)
        {
            return bestLag;
        }

        foreach (int candidate in new[] { bestLag / 2, bestLag * 2 })
        {
            if (candidate < minLag || candidate > maxLag)
            {
                continue;
            }

            double candidateBpm = 60.0 * framesPerSecond / candidate;
            bool candidatePreferred = candidateBpm is >= PreferredMinBpm and <= PreferredMaxBpm;

            if (candidatePreferred && correlation[candidate] >= correlation[bestLag] * (1 - OctavePreferenceTolerance))
            {
                return candidate;
            }
        }

        return bestLag;
    }

    private static double RefinePeak(double[] correlation, int peakLag, int minLag, int maxLag)
    {
        // Parabolic interpolation around the discrete peak for sub-frame lag accuracy.
        if (peakLag <= minLag || peakLag >= maxLag)
        {
            return peakLag;
        }

        double left = correlation[peakLag - 1];
        double centre = correlation[peakLag];
        double right = correlation[peakLag + 1];
        double denominator = left - (2 * centre) + right;

        if (Math.Abs(denominator) < 1e-12)
        {
            return peakLag;
        }

        double shift = 0.5 * (left - right) / denominator;

        return peakLag + Math.Clamp(shift, -0.5, 0.5);
    }

    private static double EstimateBeatPhase(double[] envelope, double framesPerSecond, double beatLengthSeconds)
    {
        double periodFrames = beatLengthSeconds * framesPerSecond;
        int candidateCount = Math.Max(1, (int)Math.Round(periodFrames));

        double bestScore = double.MinValue;
        int bestOffset = 0;

        // Fold the envelope at the beat period: the offset whose comb collects the
        // most onset energy is where the beats sit.
        for (int offset = 0; offset < candidateCount; offset++)
        {
            double score = 0;

            for (double position = offset; position < envelope.Length; position += periodFrames)
            {
                score += envelope[(int)position];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        double offsetSeconds = bestOffset / framesPerSecond;

        return offsetSeconds % beatLengthSeconds;
    }
}
