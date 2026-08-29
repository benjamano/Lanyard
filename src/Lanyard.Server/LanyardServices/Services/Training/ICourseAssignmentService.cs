using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Training;

public interface ICourseAssignmentService
{
    Task<Result<CourseAssignment>> AssignCourseAsync(Guid courseId, string userId, string assignedByUserId, DateTime? dueDate, LocationScope scope, bool sendAssignedEmail = true);
    /// <summary>
    /// Retrieves a user's assignments. Pass <paramref name="scope"/> when a manager is viewing
    /// someone else's record, so only that manager's location is returned; leave it null for the
    /// signed-in user viewing their own training, where every location's assignments should show.
    /// </summary>
    Task<Result<List<CourseAssignment>>> GetAssignmentsForUserAsync(string userId, LocationScope? scope = null);
    Task<Result<CourseAssignment>> GetAssignmentAsync(Guid assignmentId, string requestingUserId);
    Task<Result<CourseAssignment>> StartAssignmentAsync(Guid assignmentId, string requestingUserId);
    Task<Result<QuizGradeResult>> SubmitQuizAttemptAsync(Guid assignmentId, string requestingUserId, Dictionary<Guid, Guid> answers);
    Task<Result<List<CourseAssignment>>> GetAssignmentsForCourseAsync(Guid courseId, LocationScope scope);
    Task<Result<BulkAssignResult>> AssignCourseToUsersAsync(Guid courseId, List<string> userIds, string? assignedByUserId, DateTime? dueDate, LocationScope scope, bool sendAssignedEmail = true);
    Task<Result<CourseAssignment>> UpdateAssignmentDueDateAsync(Guid assignmentId, DateTime? newDueDate);
    Task<Result<bool>> UnassignAsync(Guid assignmentId);
    Task<Result<int>> UnassignAllForUserAsync(string userId);
    Task<Result<List<CourseAssignment>>> GetAssignmentsDueForRecurrenceAsync();
    Task<Result<CourseAssignment>> ProcessRecurrenceCycleAsync(Guid previousAssignmentId);
    Task<Result<List<CourseAssignment>>> GetAssignmentsDueSoonAsync(int daysThreshold);
    Task<Result<bool>> MarkDueSoonReminderSentAsync(Guid assignmentId);
    Task<Result<bool>> RecordSectionTransitionAsync(Guid assignmentId, string requestingUserId, Guid? departedSectionId, Guid? arrivedSectionId, DateTime transitionTimeUtc);
    Task<Result<CourseTimingSummary>> GetCourseTimingSummaryAsync(Guid courseId, LocationScope scope);
    Task<Result<List<AssignmentSectionTiming>>> GetSectionTimingsForAssignmentAsync(Guid assignmentId);
}
