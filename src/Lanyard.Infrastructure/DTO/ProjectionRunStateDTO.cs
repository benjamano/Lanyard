using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO;

/// <summary>
/// A point-in-time snapshot of one projection program run, keyed by the
/// (ClientId, DisplayIndex) pair the runner uses - the same client can run a
/// different program on each of its physical displays at the same time.
/// </summary>
public class ProjectionRunState
{
    public required Guid RunId { get; init; }

    public required Guid ClientId { get; init; }
    public required int DisplayIndex { get; init; }

    public required Guid ProgramId { get; init; }
    public required string ProgramName { get; init; }

    /// <summary>
    /// The steps as snapshotted when the run started, in play order. Kiosk pages
    /// render against this rather than re-reading the database, so a step index
    /// reported by <c>OnProgramStepAdvanced</c> always resolves to the same step
    /// the runner is holding.
    /// </summary>
    public required IReadOnlyList<ProjectionProgramStep> Steps { get; init; }

    public required int CurrentStepIndex { get; init; }

    public required bool IsPaused { get; init; }

    public required bool IsTemporaryTrigger { get; init; }
}
