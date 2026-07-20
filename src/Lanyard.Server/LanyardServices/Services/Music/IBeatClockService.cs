namespace Lanyard.Application.Services;

/// <summary>
/// Translates a client's current playback state into beat-grid delays for
/// BPM-synced DMX scene stepping.
/// </summary>
public interface IBeatClockService
{
    /// <summary>
    /// Delay until the next BPM-synced step boundary for this client, spanning
    /// <paramref name="beats"/> beats. Null when nothing is playing, the current
    /// song has no known BPM, or the inputs are invalid — callers fall back to
    /// the step's fixed Duration.
    /// </summary>
    Task<TimeSpan?> GetDelayUntilNextStepAsync(Guid clientId, double beats);
}
