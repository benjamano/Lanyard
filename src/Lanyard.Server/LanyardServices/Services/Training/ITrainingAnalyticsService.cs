using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;

namespace Lanyard.Application.Services.Training;

public interface ITrainingAnalyticsService
{
    Task<Result<List<TraineeScoreRankingRow>>> GetTopScoringTraineesAsync(Guid courseId, LocationScope scope, int topN = 10);
    Task<Result<List<TraineeTimingRankingRow>>> GetFastestCompletionsAsync(Guid courseId, LocationScope scope, int topN = 10);
    Task<Result<List<TraineeTimingRankingRow>>> GetSlowestCompletionsAsync(Guid courseId, LocationScope scope, int topN = 10);
    Task<Result<CourseCompletionSummary>> GetCourseCompletionSummaryAsync(Guid courseId, LocationScope scope);
}
