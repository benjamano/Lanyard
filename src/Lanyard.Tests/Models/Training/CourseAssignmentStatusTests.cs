using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Models.Training;

[TestClass]
public class CourseAssignmentStatusTests
{
    [TestMethod]
    public void GetStatus_ReturnsNotStarted_WhenNeverStarted()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1" };

        Assert.AreEqual(CourseAssignmentStatus.NotStarted, assignment.GetStatus());
    }

    [TestMethod]
    public void GetStatus_ReturnsInProgress_WhenStartedButNotCompleted()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1", StartedDate = DateTime.UtcNow };

        Assert.AreEqual(CourseAssignmentStatus.InProgress, assignment.GetStatus());
    }

    [TestMethod]
    public void GetStatus_ReturnsCompleted_WhenCompletedDateSet()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1", StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };

        Assert.AreEqual(CourseAssignmentStatus.Completed, assignment.GetStatus());
    }

    [TestMethod]
    public void GetStatus_ReturnsOverdue_WhenDueDatePassedAndNotCompleted()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1", DueDate = DateTime.UtcNow.AddDays(-1) };

        Assert.AreEqual(CourseAssignmentStatus.Overdue, assignment.GetStatus());
    }

    [TestMethod]
    public void GetStatus_ReturnsCompleted_WhenCompletedEvenIfPastDueDate()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1", DueDate = DateTime.UtcNow.AddDays(-1), CompletedDate = DateTime.UtcNow };

        Assert.AreEqual(CourseAssignmentStatus.Completed, assignment.GetStatus());
    }

    [TestMethod]
    public void GetStatus_DoesNotReturnOverdue_WhenDueDateIsEndOfTodayButTimeOfDayHasPassed()
    {
        CourseAssignment assignment = new() { Id = Guid.NewGuid(), CourseId = Guid.NewGuid(), UserId = "u1", DueDate = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1) };

        Assert.AreNotEqual(CourseAssignmentStatus.Overdue, assignment.GetStatus());
    }
}
