namespace Lanyard.Infrastructure.DTO.Charts
{
    // One labeled data point - the shape every reusable chart component
    // consumes. Deliberately domain-agnostic: no CourseId, no UserId - just
    // a label and a number, so any future widget/report can reuse these
    // chart components without a training dependency.
    public record ChartCategoryValue(string Label, double Value);
}
