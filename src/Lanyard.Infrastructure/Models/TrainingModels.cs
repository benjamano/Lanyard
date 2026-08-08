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

        public bool IsActive { get; set; }

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

        public bool IsActive { get; set; }

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
}
