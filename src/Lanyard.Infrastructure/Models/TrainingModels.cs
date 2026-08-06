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
}
