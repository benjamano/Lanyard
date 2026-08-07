using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Training;

public interface ICourseAssignmentService
{
    Task<Result<CourseAssignment>> AssignCourseAsync(Guid courseId, string userId, string assignedByUserId, DateTime? dueDate);
    Task<Result<List<CourseAssignment>>> GetAssignmentsForUserAsync(string userId);
    Task<Result<CourseAssignment>> GetAssignmentAsync(Guid assignmentId, string requestingUserId);
    Task<Result<CourseAssignment>> StartAssignmentAsync(Guid assignmentId, string requestingUserId);
    Task<Result<QuizGradeResult>> SubmitQuizAttemptAsync(Guid assignmentId, string requestingUserId, Dictionary<Guid, Guid> answers);
}
