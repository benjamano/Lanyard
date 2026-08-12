using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Training;

public class TrainingAnalyticsService(IDbContextFactory<ApplicationDbContext> factory) : ITrainingAnalyticsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<TraineeScoreRankingRow>>> GetTopScoringTraineesAsync(Guid courseId, LocationScope scope, int topN = 10)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> assignments = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Attempts)
                .Where(x => x.CourseId == courseId && x.IsActive && x.Attempts.Count > 0 && (scope.IsAdmin || x.LocationId == scope.LocationId))
                .ToListAsync();

            List<TraineeScoreRankingRow> rows = [.. assignments
                .Select(x =>
                {
                    CourseQuizAttempt latest = x.Attempts.OrderByDescending(a => a.AttemptNumber).First();
                    return new TraineeScoreRankingRow(x.UserId, latest.ScorePercent, latest.AttemptNumber, latest.SubmittedDate);
                })
                .OrderByDescending(x => x.ScorePercent)
                .ThenBy(x => x.SubmittedDate)
                .Take(topN)];

            return Result<List<TraineeScoreRankingRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            return Result<List<TraineeScoreRankingRow>>.Fail($"Failed to retrieve top scoring trainees: {ex.Message}");
        }
    }

    public Task<Result<List<TraineeTimingRankingRow>>> GetFastestCompletionsAsync(Guid courseId, LocationScope scope, int topN = 10) =>
        GetTimingRankingAsync(courseId, scope, topN, descending: false);

    public Task<Result<List<TraineeTimingRankingRow>>> GetSlowestCompletionsAsync(Guid courseId, LocationScope scope, int topN = 10) =>
        GetTimingRankingAsync(courseId, scope, topN, descending: true);

    private async Task<Result<List<TraineeTimingRankingRow>>> GetTimingRankingAsync(Guid courseId, LocationScope scope, int topN, bool descending)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> completed = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CourseId == courseId && x.IsActive && x.StartedDate != null && x.CompletedDate != null && (scope.IsAdmin || x.LocationId == scope.LocationId))
                .ToListAsync();

            IEnumerable<TraineeTimingRankingRow> rows = completed
                .Select(x => new TraineeTimingRankingRow(x.UserId, (x.CompletedDate!.Value - x.StartedDate!.Value).TotalMinutes, x.CompletedDate.Value));

            rows = descending
                ? rows.OrderByDescending(x => x.DurationMinutes)
                : rows.OrderBy(x => x.DurationMinutes);

            return Result<List<TraineeTimingRankingRow>>.Ok([.. rows.Take(topN)]);
        }
        catch (Exception ex)
        {
            return Result<List<TraineeTimingRankingRow>>.Fail($"Failed to retrieve completion timings: {ex.Message}");
        }
    }

    public async Task<Result<CourseCompletionSummary>> GetCourseCompletionSummaryAsync(Guid courseId, LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? course = await ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == courseId);

            if (course is null)
            {
                return Result<CourseCompletionSummary>.Fail("Course not found.");
            }

            List<CourseAssignment> assignments = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Attempts)
                .Where(x => x.CourseId == courseId && x.IsActive && (scope.IsAdmin || x.LocationId == scope.LocationId))
                .ToListAsync();

            int notStarted = assignments.Count(x => x.GetStatus() == CourseAssignmentStatus.NotStarted);
            int inProgress = assignments.Count(x => x.GetStatus() == CourseAssignmentStatus.InProgress);
            int overdue = assignments.Count(x => x.GetStatus() == CourseAssignmentStatus.Overdue);
            int completed = assignments.Count(x => x.GetStatus() == CourseAssignmentStatus.Completed);

            List<CourseAssignment> attempted = [.. assignments.Where(x => x.Attempts.Count > 0)];

            // CompletedCount and passedLatest always agree, since an assignment
            // only completes the moment its latest attempt passes - kept as
            // separate computations because they answer different questions
            // (status bucket vs. latest-attempt outcome), not because they can diverge.
            int passedLatest = attempted.Count(x => x.Attempts.OrderByDescending(a => a.AttemptNumber).First().Passed);
            int failedLatest = attempted.Count - passedLatest;

            return Result<CourseCompletionSummary>.Ok(new CourseCompletionSummary(
                assignments.Count, notStarted, inProgress, overdue, completed,
                attempted.Count, passedLatest, failedLatest, course.PassMarkPercent));
        }
        catch (Exception ex)
        {
            return Result<CourseCompletionSummary>.Fail($"Failed to retrieve course completion summary: {ex.Message}");
        }
    }
}
