namespace Lanyard.Application.Services;

/// <summary>
/// Pure beat-grid arithmetic for BPM-synced DMX scene stepping. No dependencies,
/// so the timing behaviour is fully unit-testable without a running player.
/// </summary>
public static class BeatMath
{
    // The playback position is an estimate that gets re-anchored by periodic client
    // reports, and the client's reader position moves in audio-buffer-sized chunks —
    // so between two step computations the position can jump by a few hundred ms in
    // either direction. Chasing the next grid boundary exactly therefore double-fires
    // (a backwards jump re-exposes a boundary that already fired) or skips. Instead,
    // each step lasts one nominal step length, corrected toward the grid by at most
    // this fraction — delays stay within [1-f, 1+f] × step length, so jitter can
    // never double-fire or skip, and the phase error shrinks every step until the
    // advances sit on the beat.
    private const double MaxPhaseCorrectionFraction = 0.25;

    /// <summary>
    /// Seconds the current step should hold so that step advances converge onto the
    /// beat grid (boundaries every <paramref name="beats"/> × beat-length seconds,
    /// anchored at <paramref name="firstBeatOffsetSeconds"/>). Call at the moment a
    /// step fires, passing the playback position at that moment. Null for
    /// non-positive bpm/beats — callers fall back to the step's fixed Duration.
    /// </summary>
    public static double? SecondsUntilNextStep(
        double positionSeconds,
        double bpm,
        double firstBeatOffsetSeconds,
        double beats)
    {
        if (bpm <= 0 || beats <= 0)
        {
            return null;
        }

        double beatLength = 60.0 / bpm;
        double stepLength = beats * beatLength;
        double relativePosition = positionSeconds - firstBeatOffsetSeconds;

        // Positive modulo: the position may precede the first beat (relativePosition < 0).
        double intoStep = ((relativePosition % stepLength) + stepLength) % stepLength;

        // Signed phase error of this fire relative to the nearest boundary:
        // positive = fired late (boundary behind us), negative = fired early
        // (boundary still ahead). Range (-stepLength/2, stepLength/2].
        double firedLateBy = intoStep <= stepLength / 2 ? intoStep : intoStep - stepLength;

        double maxCorrection = MaxPhaseCorrectionFraction * stepLength;
        double correction = Math.Clamp(firedLateBy, -maxCorrection, maxCorrection);

        return stepLength - correction;
    }
}
