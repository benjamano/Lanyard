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
    Task<Result<List<CourseAssignment>>> GetAssignmentsForCourseAsync(Guid courseId);
    Task<Result<BulkAssignResult>> AssignCourseToUsersAsync(Guid courseId, List<string> userIds, string? assignedByUserId, DateTime? dueDate);
    Task<Result<CourseAssignment>> UpdateAssignmentDueDateAsync(Guid assignmentId, DateTime? newDueDate);
    Task<Result<bool>> UnassignAsync(Guid assignmentId);
}
