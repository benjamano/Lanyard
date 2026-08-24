using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Manager.Training;

// A person can end up with more than one CourseAssignment for the same course (e.g. reassigned
// after completion), so grids group assignments under one row per Label (course or person,
// depending on the view) instead of showing one flat row per assignment.
public class TrainingAssignmentGroup
{
    public required string Label { get; set; }
    public required List<CourseAssignment> Assignments { get; set; }

    // Assignments is expected to be pre-sorted most-recent-first by the caller - the primary
    // assignment drives the collapsed row's summary columns (Status, Latest Score, Due Date).
    public CourseAssignment Primary => Assignments[0];

    public bool HasExpandableDetails => Assignments.Count > 1 || Primary.Attempts.Count > 0;

    // Built once whenever the source assignments change, rather than recomputed on every render -
    // FluentDataGrid tracks which row is expanded by the TrainingAssignmentGroup instance itself,
    // and Blazor auto-rerenders a component after any event handler on it completes (e.g. clicking
    // any button in a row), so a property that rebuilds fresh instances every render would silently
    // collapse every expanded row on the very next click, anywhere on the page. Callers must cache
    // the result rather than calling this from a `=>` property.
    public static List<TrainingAssignmentGroup> BuildGroups<TKey>(
        IEnumerable<CourseAssignment> assignments,
        Func<CourseAssignment, TKey> keySelector,
        Func<IGrouping<TKey, CourseAssignment>, string> labelSelector)
    {
        return assignments
            .GroupBy(keySelector)
            .Select(g => new TrainingAssignmentGroup
            {
                Label = labelSelector(g),
                Assignments = g.OrderByDescending(x => x.AssignedDate).ToList()
            })
            .OrderBy(g => g.Label)
            .ToList();
    }
}
