using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

/// <summary>
/// Owns the step/timing loop for projection programs, so the server - not a single
/// kiosk browser circuit - is the authority on what is playing where. Mirrors
/// <see cref="IDmxSceneRunnerService"/>; runs are keyed by (clientId, displayIndex)
/// because one client can project a different program on each display at once.
/// </summary>
public interface IProjectionProgramRunnerService
{
    /// <summary>clientId, displayIndex, programId</summary>
    event Action<Guid, int, Guid>? OnProgramStarted;

    /// <summary>clientId, displayIndex, stepIndex (into the run's snapshotted step list)</summary>
    event Action<Guid, int, int>? OnProgramStepAdvanced;

    /// <summary>clientId, displayIndex, isPaused</summary>
    event Action<Guid, int, bool>? OnProgramPauseChanged;

    /// <summary>clientId, displayIndex, programId</summary>
    event Action<Guid, int, Guid>? OnProgramStopped;

    /// <summary>
    /// clientId, displayIndex, programId - raised when a temporary (triggered) run finishes
    /// its configured repeats without being stopped, so the kiosk window can be closed and
    /// the client's ambient projection settings restored. Ambient runs have no window to
    /// close, so they only raise <see cref="OnProgramStopped"/>.
    /// </summary>
    event Action<Guid, int, Guid>? OnProgramCompletedNaturally;

    /// <summary>
    /// Starts (or restarts) a program on a client's display. Returns the run id, which
    /// <see cref="Stop"/> accepts so a departing kiosk circuit can only ever stop
    /// its own run and never one started by a page that replaced it.
    /// </summary>
    Task<Result<Guid>> StartAsync(Guid clientId, int displayIndex, Guid programId, bool repeatInfinitely, int repeatCount, bool isTemporaryTrigger);

    Result<bool> Pause(Guid clientId, int displayIndex);
    Result<bool> Resume(Guid clientId, int displayIndex);

    /// <summary>Jumps playback to <paramref name="stepIndex"/> in the run's snapshotted step list.</summary>
    Result<bool> SkipToStep(Guid clientId, int displayIndex, int stepIndex);

    Result<bool> SkipToNextStep(Guid clientId, int displayIndex);
    Result<bool> SkipToPreviousStep(Guid clientId, int displayIndex);

    /// <summary>
    /// Stops the run on a display. When <paramref name="runId"/> is supplied the call is a
    /// no-op unless it matches the run currently on that display.
    /// </summary>
    Result<bool> Stop(Guid clientId, int displayIndex, Guid? runId = null);

    ProjectionRunState? GetRunningState(Guid clientId, int displayIndex);

    List<ProjectionRunState> GetRunningStates();
}
