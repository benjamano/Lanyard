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

    Task<Result<CourseSection>> SaveSectionAsync(CourseSection section);
    Task<Result<bool>> DeleteSectionAsync(Guid sectionId);
    Task<Result<bool>> ReorderSectionsAsync(Guid courseId, List<Guid> orderedSectionIds);

    Task<Result<CourseQuestion>> SaveQuestionAsync(CourseQuestion question);
    Task<Result<bool>> DeleteQuestionAsync(Guid questionId);
    Task<Result<bool>> ReorderQuestionsAsync(Guid courseId, List<Guid> orderedQuestionIds);
}
