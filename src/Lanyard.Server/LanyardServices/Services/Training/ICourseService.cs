using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Training;

public interface ICourseService
{
    Task<Result<List<Course>>> GetCoursesAsync(LocationScope scope);
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
