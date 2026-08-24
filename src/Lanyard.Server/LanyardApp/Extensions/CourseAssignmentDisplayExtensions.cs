using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Extensions;

public static class CourseAssignmentDisplayExtensions
{
    public static string GetStatusLabel(this CourseAssignment assignment) => assignment.GetStatus() switch
    {
        CourseAssignmentStatus.NotStarted => "Not Started",
        CourseAssignmentStatus.InProgress => "In Progress",
        CourseAssignmentStatus.Completed => "Completed",
        CourseAssignmentStatus.Overdue => "Overdue",
        _ => "Unknown"
    };

    public static BadgeColor GetStatusColor(this CourseAssignment assignment) => assignment.GetStatus() switch
    {
        CourseAssignmentStatus.NotStarted => BadgeColor.Subtle,
        CourseAssignmentStatus.InProgress => BadgeColor.Informative,
        CourseAssignmentStatus.Completed => BadgeColor.Success,
        CourseAssignmentStatus.Overdue => BadgeColor.Danger,
        _ => BadgeColor.Subtle
    };
}
