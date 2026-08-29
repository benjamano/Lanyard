using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Training;

public interface ICourseService
{
    // allLocations only means anything for an Admin: it lifts the location filter so they can
    // review every location's courses at once. Everyone else is always filtered to their own
    // location (plus courses shared from a sibling location), whatever is passed here.
    Task<Result<List<Course>>> GetCoursesAsync(LocationScope scope, bool allLocations = false);
    Task<Result<Course>> GetCourseAsync(Guid courseId, LocationScope scope);
    Task<Result<Course>> SaveCourseAsync(Course course, LocationScope scope);
    Task<Result<bool>> DeleteCourseAsync(Guid courseId, LocationScope scope);

    Task<Result<CourseSection>> SaveSectionAsync(CourseSection section, LocationScope scope);
    Task<Result<bool>> DeleteSectionAsync(Guid sectionId, LocationScope scope);
    Task<Result<bool>> ReorderSectionsAsync(Guid courseId, List<Guid> orderedSectionIds, LocationScope scope);

    Task<Result<CourseQuestion>> SaveQuestionAsync(CourseQuestion question, LocationScope scope);
    Task<Result<bool>> DeleteQuestionAsync(Guid questionId, LocationScope scope);
    Task<Result<bool>> ReorderQuestionsAsync(Guid courseId, List<Guid> orderedQuestionIds, LocationScope scope);
}
