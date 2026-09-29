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

            // Ranking, latest-attempt selection and the top-N cut all happen in SQL; this used to
            // load every assignment and every attempt for the course to keep ten rows.
            var ranked = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CourseId == courseId && x.IsActive && x.Attempts.Count > 0 && (scope.IsAdmin || x.LocationId == scope.LocationId))
                .Select(x => new
                {
                    x.UserId,
                    Latest = x.Attempts.OrderByDescending(a => a.AttemptNumber).First()
                })
                .OrderByDescending(x => x.Latest.ScorePercent)
                .ThenBy(x => x.Latest.SubmittedDate)
                .Take(topN)
                .ToListAsync();

            List<TraineeScoreRankingRow> rows = [.. ranked
                .Select(x => new TraineeScoreRankingRow(x.UserId, x.Latest.ScorePercent, x.Latest.AttemptNumber, x.Latest.SubmittedDate))];

            return Result<List<TraineeScoreRankingRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            return Result<List<TraineeScoreRankingRow>>.Fail($"Failed to retrieve top scoring trainees: {ex.Message}");
        }
    }

    public Task<Result<List<TraineeTimingRankingRow>>> GetFastestCompletionsAsync(Guid courseId, LocationScope scope, int topN = 10) =>
        GetTimingRankingAsync(courseId, scope, topN);

    private async Task<Result<List<TraineeTimingRankingRow>>> GetTimingRankingAsync(Guid courseId, LocationScope scope, int topN)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var fastest = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CourseId == courseId && x.IsActive && x.StartedDate != null && x.CompletedDate != null && (scope.IsAdmin || x.LocationId == scope.LocationId))
                .OrderBy(x => x.CompletedDate!.Value - x.StartedDate!.Value)
                .Take(topN)
                .Select(x => new { x.UserId, x.StartedDate, x.CompletedDate })
                .ToListAsync();

            List<TraineeTimingRankingRow> rows = [.. fastest
                .Select(x => new TraineeTimingRankingRow(x.UserId, (x.CompletedDate!.Value - x.StartedDate!.Value).TotalMinutes, x.CompletedDate.Value))];

            return Result<List<TraineeTimingRankingRow>>.Ok(rows);
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
