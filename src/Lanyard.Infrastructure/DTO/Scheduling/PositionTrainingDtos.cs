namespace Lanyard.Infrastructure.DTO.Scheduling;

// How many people in a position are up to date on a set of courses right now. Shown under the
// required-training picker, worked out from the ticked courses before they're saved, so a manager
// sees how many people a requirement would stop picking up shifts. LocationName is set when it
// only counts one location's staff (a Manager's view).
public record TrainingCoverage(int Holders, int UpToDate, string? LocationName);

// What auto-assigning a position's required training did: the courses given out (and to how many
// people), how many people were already on a course, and how many couldn't be given one - a
// Manager can only assign to their own location's staff, and only courses they can see.
public record RequiredTrainingAssignResult(List<AssignedCourse> Courses, int AlreadyOnIt, int CouldNotAssign)
{
    public static RequiredTrainingAssignResult Empty { get; } = new([], 0, 0);

    public int Assigned => Courses.Sum(x => x.People);

    // "Assigned Fire Safety to 2 people and First Aid to 1 person. 1 couldn't be assigned from your
    // location." - null when nothing happened worth saying.
    public string? Summary()
    {
        List<string> parts = [];

        if (Courses.Count > 0)
        {
            List<string> each = Courses.Select(x => $"{x.Name} to {x.People} {(x.People == 1 ? "person" : "people")}").ToList();
            string joined = each.Count == 1 ? each[0] : $"{string.Join(", ", each.Take(each.Count - 1))} and {each[^1]}";
            parts.Add($"Assigned {joined}.");
        }
        else if (AlreadyOnIt > 0)
        {
            parts.Add("Nobody needed it assigning - they're already on it or have passed it.");
        }

        if (CouldNotAssign > 0)
        {
            parts.Add($"{CouldNotAssign} {(CouldNotAssign == 1 ? "assignment" : "assignments")} couldn't be made from your location - an admin can assign {(CouldNotAssign == 1 ? "it" : "them")} under Training.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}

public record AssignedCourse(Guid CourseId, string Name, int People);

// SetUserPositionsAsync's result: the person's positions now, and the training that was assigned
// because of any new ones.
public record UserPositionsSaved(List<Lanyard.Infrastructure.Models.UserPosition> Positions, RequiredTrainingAssignResult Training);

// ApplyPositionsAsync's result: how many people changed, and the training assigned for the
// positions they were given.
public record BulkPositionsApplied(int Changed, RequiredTrainingAssignResult Training);
