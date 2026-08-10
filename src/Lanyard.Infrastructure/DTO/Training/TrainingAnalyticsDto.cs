namespace Lanyard.Infrastructure.DTO.Training
{
    // UserId only, by design - the Razor layer resolves display names via
    // ISecurityService.GetAllUsersAsync()/.GetName(), same as
    // ByPersonTrainingTab.razor. Keeps this service DB-join-free and fast.
    public record TraineeScoreRankingRow(string UserId, int ScorePercent, int AttemptNumber, DateTime SubmittedDate);

    public record TraineeTimingRankingRow(string UserId, double DurationMinutes, DateTime CompletedDate);

    public record CourseCompletionSummary(
        int TotalAssignments,
        int NotStartedCount,
        int InProgressCount,
        int OverdueCount,
        int CompletedCount,
        int AttemptedCount,
        int LatestAttemptPassedCount,
        int LatestAttemptFailedCount,
        int PassMarkPercent);
}
