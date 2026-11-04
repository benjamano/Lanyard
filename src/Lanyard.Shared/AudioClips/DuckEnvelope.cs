namespace Lanyard.Shared.AudioClips;

/// <summary>
/// The music's duck level over time: 1 is its normal level, 0.2 is a fifth of it. Each change
/// fades in a straight line from wherever the level is at that moment, so a clip that cuts in
/// mid-fade, or a restore that starts before the fade down finished, never jumps.
/// </summary>
/// <remarks>
/// Pure and clock-free (callers pass a monotonic "now"), so the kiosk's audio thread can read it
/// every buffer while the clip player changes it, and tests can drive it without waiting.
/// </remarks>
public sealed class DuckEnvelope
{
    private sealed record Ramp(float From, float To, TimeSpan Start, TimeSpan Duration);

    private Ramp _ramp = new(1f, 1f, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The level the music is heading to: below 1 while ducked or ducking.</summary>
    public float Target => Volatile.Read(ref _ramp).To;

    public bool IsDucked => Target < 1f;

    public float GainAt(TimeSpan now)
    {
        Ramp ramp = Volatile.Read(ref _ramp);

        if (ramp.Duration <= TimeSpan.Zero || now >= ramp.Start + ramp.Duration)
        {
            return ramp.To;
        }

        if (now <= ramp.Start)
        {
            return ramp.From;
        }

        float progress = (float)((now - ramp.Start).TotalMilliseconds / ramp.Duration.TotalMilliseconds);

        return ramp.From + (ramp.To - ramp.From) * progress;
    }

    public void DuckTo(float level, TimeSpan fade, TimeSpan now)
    {
        float target = Math.Clamp(level, 0f, 1f);
        TimeSpan duration = fade < TimeSpan.Zero ? TimeSpan.Zero : fade;

        Volatile.Write(ref _ramp, new Ramp(GainAt(now), target, now, duration));
    }

    public void Restore(TimeSpan fade, TimeSpan now) => DuckTo(1f, fade, now);
}
