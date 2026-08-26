using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    public class Course
    {
        public Guid Id { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public int PassMarkPercent { get; set; } = 80;
        public bool AutoAssignOnUserCreation { get; set; }

        // Null = does not recur. When set, everyone who has ever completed this
        // course is due to retake it this many months after their CompletedDate.
        public int? RecurrenceMonths { get; set; }

        public bool IsActive { get; set; }

        // Nullable for now: existing courses need a location assigned through the
        // CourseEditor UI before this can become required. See plan Task 3 note.
        public int? LocationId { get; set; }
        public Location? Location { get; set; }

        public bool IsShared { get; set; }

        public virtual List<CourseSection> Sections { get; set; } = [];
        public virtual List<CourseQuestion> Questions { get; set; } = [];
    }

    public class CourseSection
    {
        public Guid Id { get; set; }

        public required Guid CourseId { get; set; }
        public Course? Course { get; set; }

        public required string Title { get; set; }
        public string BodyHtml { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public bool IsActive { get; set; }
    }

    public class CourseQuestion
    {
        public Guid Id { get; set; }

        public required Guid CourseId { get; set; }
        public Course? Course { get; set; }

        public required string QuestionText { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; }

        public virtual List<CourseQuestionOption> Options { get; set; } = [];
    }

    public class CourseQuestionOption
    {
        public Guid Id { get; set; }

        public required Guid QuestionId { get; set; }
        public CourseQuestion? Question { get; set; }

        public required string OptionText { get; set; }
        public bool IsCorrect { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; }
    }

    public class CourseAssignment
    {
        public Guid Id { get; set; }

        public required Guid CourseId { get; set; }
        public Course? Course { get; set; }

        public required string UserId { get; set; }
        public string? AssignedByUserId { get; set; }

        public DateTime AssignedDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? StartedDate { get; set; }
        public DateTime? CompletedDate { get; set; }

        // Set once the training-due-soon reminder email has been sent for the current
        // DueDate, so the periodic sweep in TrainingDueSoonHostedService doesn't re-send it
        // every cycle. Reset to null by CourseAssignmentService.UpdateAssignmentDueDateAsync
        // whenever the due date changes, so a pushed-out date can trigger a fresh reminder.
        public DateTime? DueSoonReminderSentDate { get; set; }

        public bool IsActive { get; set; }

        // Nullable for now, same reasoning as Course.LocationId (see Task 3).
        // For non-Admin assigners this is the acting manager's own location, NOT
        // necessarily the course's location - a course can be shared.
        public int? LocationId { get; set; }
        public Location? Location { get; set; }

        public virtual List<CourseQuizAttempt> Attempts { get; set; } = [];

        public CourseAssignmentStatus GetStatus()
        {
            if (CompletedDate is not null)
            {
                return CourseAssignmentStatus.Completed;
            }

            if (DueDate is not null && DueDate < DateTime.UtcNow)
            {
                return CourseAssignmentStatus.Overdue;
            }

            return StartedDate is null ? CourseAssignmentStatus.NotStarted : CourseAssignmentStatus.InProgress;
        }
    }

    public class CourseQuizAttempt
    {
        public Guid Id { get; set; }

        public required Guid AssignmentId { get; set; }
        public CourseAssignment? Assignment { get; set; }

        public int AttemptNumber { get; set; }
        public DateTime SubmittedDate { get; set; }
        public int ScorePercent { get; set; }
        public bool Passed { get; set; }

        public virtual List<CourseQuizAttemptAnswer> Answers { get; set; } = [];
    }

    public class CourseQuizAttemptAnswer
    {
        public Guid Id { get; set; }

        public required Guid AttemptId { get; set; }
        public CourseQuizAttempt? Attempt { get; set; }

        public required Guid QuestionId { get; set; }
        public required Guid SelectedOptionId { get; set; }

        public bool WasCorrect { get; set; }
    }

    // One row per (AssignmentId, SectionId), first-entered/last-left - not a
    // full visit log. EnteredDate is set once, on first arrival; LeftDate is
    // overwritten on every departure. A section never departed (tab closed
    // mid-read) keeps LeftDate null and is excluded from duration averages.
    public class CourseSectionProgress
    {
        public Guid Id { get; set; }

        public required Guid AssignmentId { get; set; }
        public CourseAssignment? Assignment { get; set; }

        public required Guid SectionId { get; set; }
        public CourseSection? Section { get; set; }

        public DateTime EnteredDate { get; set; }
        public DateTime? LeftDate { get; set; }
    }
}
