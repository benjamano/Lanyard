using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;

namespace Lanyard.Application.Services.Training;

public interface ITrainingAnalyticsService
{
    Task<Result<List<TraineeScoreRankingRow>>> GetTopScoringTraineesAsync(Guid courseId, int topN = 10);
    Task<Result<List<TraineeTimingRankingRow>>> GetFastestCompletionsAsync(Guid courseId, int topN = 10);
    Task<Result<List<TraineeTimingRankingRow>>> GetSlowestCompletionsAsync(Guid courseId, int topN = 10);
    Task<Result<CourseCompletionSummary>> GetCourseCompletionSummaryAsync(Guid courseId);
}
