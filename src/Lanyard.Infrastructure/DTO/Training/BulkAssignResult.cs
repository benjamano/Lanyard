namespace Lanyard.Infrastructure.DTO.Training
{
    /// <param name="AssignedCount">People who received a new assignment.</param>
    /// <param name="SkippedDuplicateCount">People in scope who already had an open assignment for this course.</param>
    /// <param name="SkippedOutsideLocationCount">
    /// People who were requested but are not members of the acting manager's location, so were
    /// never eligible. Tracked separately from duplicates so the UI can explain why the assigned
    /// total is lower than the number of people picked.
    /// </param>
    public record BulkAssignResult(int AssignedCount, int SkippedDuplicateCount, int SkippedOutsideLocationCount = 0);
}
