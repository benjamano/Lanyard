namespace Lanyard.Shared.Projection;

/// <summary>
/// The rules for stepping through a projection program: how long each step holds, which step
/// comes next, and when the program has finished its repeats. Shared so the server's
/// ProjectionProgramRunnerService and the kiosk's offline player can't disagree about them.
/// Pure functions only; timing and pause/skip state stay with each caller.
/// </summary>
public static class ProjectionSequence
{
    // A step with no configured hold falls back to 5s, matching the behaviour the
    // kiosk page's own loop had before playback moved server-side.
    public const int DefaultHoldMilliseconds = 5000;

    // How often a held step re-checks for pause/skip/stop.
    public const int TickMilliseconds = 200;

    public static TimeSpan EffectiveHold(int holdForMilliseconds)
    {
        return TimeSpan.FromMilliseconds(holdForMilliseconds == 0 ? DefaultHoldMilliseconds : holdForMilliseconds);
    }

    /// <summary>
    /// How long a hold loop sleeps before re-checking. The last delay of a hold is trimmed to
    /// what remains so the step ends on time, but only while playing: paused, the remaining time
    /// doesn't shrink, so trimming would spin every millisecond for as long as the pause lasts.
    /// </summary>
    public static int ComputeHoldDelayMilliseconds(TimeSpan hold, TimeSpan played, bool isPaused)
    {
        if (isPaused)
        {
            return TickMilliseconds;
        }

        TimeSpan remaining = hold - played;

        return (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, TickMilliseconds);
    }

    /// <summary>
    /// Where playback goes after the step at <paramref name="index"/> finishes.
    /// <paramref name="repeatCount"/> is the number of *extra* passes, so 0 plays the program once.
    /// </summary>
    public static ProjectionStepPosition Advance(int index, int stepCount, int completedPasses, bool repeatInfinitely, int repeatCount)
    {
        int next = index + 1;

        if (next < stepCount)
        {
            return new ProjectionStepPosition(next, completedPasses, Finished: false);
        }

        if (!repeatInfinitely && completedPasses >= repeatCount)
        {
            return new ProjectionStepPosition(0, completedPasses, Finished: true);
        }

        return new ProjectionStepPosition(0, completedPasses + 1, Finished: false);
    }
}

public readonly record struct ProjectionStepPosition(int Index, int CompletedPasses, bool Finished);
