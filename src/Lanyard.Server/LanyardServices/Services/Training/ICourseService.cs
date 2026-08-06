using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Training;

public interface ICourseService
{
    Task<Result<List<Course>>> GetCoursesAsync();
    Task<Result<Course>> GetCourseAsync(Guid courseId);
    Task<Result<Course>> SaveCourseAsync(Course course);
    Task<Result<bool>> DeleteCourseAsync(Guid courseId);

    Task<Result<CourseSection>> SaveSectionAsync(CourseSection section);
    Task<Result<bool>> DeleteSectionAsync(Guid sectionId);

    Task<Result<CourseQuestion>> SaveQuestionAsync(CourseQuestion question);
    Task<Result<bool>> DeleteQuestionAsync(Guid questionId);
}
