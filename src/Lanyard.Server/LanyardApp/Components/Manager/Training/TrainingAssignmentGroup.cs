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
}
