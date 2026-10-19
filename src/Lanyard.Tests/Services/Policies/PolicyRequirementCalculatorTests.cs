using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Policies;

[TestClass]
public class PolicyRequirementCalculatorTests
{
    private static readonly DateTime Published = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static Policy PolicyWith(int required = 1, int current = 1, int days = 14, int? months = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Fire policy",
        RequiredVersionNumber = required,
        CurrentVersionNumber = current,
        AcknowledgeWithinDays = days,
        ReacknowledgeEveryMonths = months
    };

    private static PolicyAssignment AssignedOn(DateTime assigned, int? acknowledgedVersion = null, DateTime? acknowledgedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = Guid.NewGuid(),
        UserId = "amy",
        AssignedDate = assigned,
        AcknowledgedVersionNumber = acknowledgedVersion,
        AcknowledgedAtUtc = acknowledgedAt
    };

    [TestMethod]
    public void Compute_NothingPublished_NothingToDo()
    {
        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(PolicyWith(required: 0, current: 0), AssignedOn(Published), null);

        Assert.IsNull(result.OutstandingFrom);
        Assert.IsNull(result.DueDate);
    }

    [TestMethod]
    public void Compute_NotYetAcknowledged_DueDaysAfterPublishing()
    {
        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(PolicyWith(days: 14), AssignedOn(Published.AddDays(-30)), Published);

        Assert.AreEqual(Published, result.OutstandingFrom);
        Assert.AreEqual(Published.AddDays(14), result.DueDate);
    }

    [TestMethod]
    public void Compute_JoinedAfterPublishing_DueDaysAfterJoining()
    {
        DateTime joined = Published.AddDays(20);

        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(PolicyWith(days: 7), AssignedOn(joined), Published);

        Assert.AreEqual(joined, result.OutstandingFrom);
        Assert.AreEqual(joined.AddDays(7), result.DueDate);
    }

    [TestMethod]
    public void Compute_AcknowledgedRequiredVersion_UpToDate()
    {
        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(PolicyWith(), AssignedOn(Published, 1, Published.AddDays(2)), Published);

        Assert.IsNull(result.OutstandingFrom);
        Assert.IsNull(result.DueDate);
    }

    [TestMethod]
    public void Compute_NewVersionRequiringReacknowledgement_OutstandingAgain()
    {
        DateTime v2Published = Published.AddDays(40);

        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(
            PolicyWith(required: 2, current: 2, days: 10), AssignedOn(Published, 1, Published.AddDays(1)), v2Published);

        Assert.AreEqual(v2Published, result.OutstandingFrom);
        Assert.AreEqual(v2Published.AddDays(10), result.DueDate);
    }

    [TestMethod]
    public void Compute_TypoFixVersion_EarlierAcknowledgementStillCounts()
    {
        // v3 was published without asking for re-acknowledgement, so v2 is still the required one.
        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(
            PolicyWith(required: 2, current: 3), AssignedOn(Published, 2, Published.AddDays(1)), Published);

        Assert.IsNull(result.OutstandingFrom);
    }

    [TestMethod]
    public void Compute_ReacknowledgeInterval_OpensBeforeExpiryAndExpiresAfterMonths()
    {
        DateTime acknowledged = Published.AddDays(1);

        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(
            PolicyWith(days: 14, months: 12), AssignedOn(Published, 1, acknowledged), Published);

        Assert.AreEqual(acknowledged.AddMonths(12).AddDays(-14), result.OutstandingFrom);
        Assert.AreEqual(acknowledged.AddMonths(12), result.DueDate);
    }

    [TestMethod]
    public void Compute_IntervalShorterThanDeadline_NeverOpensBeforeTheAcknowledgement()
    {
        DateTime acknowledged = Published.AddDays(1);

        PolicyRequirementCalculator.Requirement result = PolicyRequirementCalculator.Compute(
            PolicyWith(days: 60, months: 1), AssignedOn(Published, 1, acknowledged), Published);

        Assert.AreEqual(acknowledged, result.OutstandingFrom);
    }

    [TestMethod]
    public void Apply_NewOutstandingPeriod_ResetsReminders()
    {
        PolicyAssignment assignment = AssignedOn(Published, 1, Published.AddDays(1));
        assignment.ReminderCount = 3;
        assignment.LastReminderSentDate = Published.AddDays(20);

        bool changed = PolicyRequirementCalculator.Apply(PolicyWith(required: 2, current: 2), assignment, Published.AddDays(40));

        Assert.IsTrue(changed);
        Assert.AreEqual(0, assignment.ReminderCount);
        Assert.IsNull(assignment.LastReminderSentDate);
    }

    [TestMethod]
    public void Apply_NoChange_LeavesRemindersAlone()
    {
        Policy policy = PolicyWith();
        PolicyAssignment assignment = AssignedOn(Published);
        PolicyRequirementCalculator.Apply(policy, assignment, Published);
        assignment.ReminderCount = 2;

        bool changed = PolicyRequirementCalculator.Apply(policy, assignment, Published);

        Assert.IsFalse(changed);
        Assert.AreEqual(2, assignment.ReminderCount);
    }

    [TestMethod]
    public void StatusAt_ReflectsTheWindow()
    {
        DateTime due = Published.AddDays(14);

        Assert.AreEqual(PolicyStatus.UpToDate, PolicyRequirementCalculator.StatusAt(null, null, Published));
        Assert.AreEqual(PolicyStatus.UpToDate, PolicyRequirementCalculator.StatusAt(Published, due, Published.AddDays(-1)));
        Assert.AreEqual(PolicyStatus.Outstanding, PolicyRequirementCalculator.StatusAt(Published, due, Published.AddDays(1)));
        Assert.AreEqual(PolicyStatus.Overdue, PolicyRequirementCalculator.StatusAt(Published, due, due.AddMinutes(1)));
    }
}
