namespace Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// Crossfades between DMX levels. Pure, so the scene runner (real lights) and the on-screen
/// preview fade the same way and it's unit tested.
/// </summary>
public static class DmxFade
{
    /// <summary>How often a fade writes a new set of levels: one DMX frame, ~40 Hz.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>The level <paramref name="progress"/> (0-1) of the way from one value to another, rounded.</summary>
    public static byte Interpolate(byte from, byte to, double progress)
    {
        double t = Math.Clamp(double.IsNaN(progress) ? 1 : progress, 0, 1);

        return (byte)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The fade actually used for a step: never longer than the step holds (a fade can't run into
    /// the next step), never negative.
    /// </summary>
    public static TimeSpan Effective(TimeSpan fade, TimeSpan hold)
    {
        if (fade <= TimeSpan.Zero || hold <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return fade < hold ? fade : hold;
    }

    /// <summary>
    /// The channels that change at this point of a fade: one entry per (address, from, to), only
    /// those whose level differs from the previous frame's, so a fade sends nothing that stays put.
    /// </summary>
    public static List<DmxChannel> Frame(IReadOnlyList<(int Address, byte From, byte To)> channels, double progress, double previousProgress)
    {
        List<DmxChannel> frame = [];

        foreach ((int address, byte from, byte to) in channels)
        {
            byte now = Interpolate(from, to, progress);

            if (previousProgress < 0 || now != Interpolate(from, to, previousProgress))
            {
                frame.Add(new DmxChannel { Address = address, Value = now });
            }
        }

        return frame;
    }
}
