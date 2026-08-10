namespace Lanyard.Infrastructure.DTO.Training
{
    public record SectionTimingSummary(Guid SectionId, string SectionTitle, double AverageMinutes, int SampleCount);

    public record CourseTimingSummary(double? AverageTotalMinutes, int CompletedCount, List<SectionTimingSummary> Sections);

    public record AssignmentSectionTiming(Guid SectionId, string SectionTitle, DateTime EnteredDate, DateTime? LeftDate, double? DurationMinutes);
}
