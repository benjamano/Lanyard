using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Policies;

// The one place that decides when someone has to (re-)acknowledge a policy. Everything else -
// publish, acknowledge, the audience sync, the reminder sweep - calls Apply and reads the dates it
// leaves on the PolicyAssignment, so Compliance and My Policies can never disagree.
public static class PolicyRequirementCalculator
{
    public readonly record struct Requirement(DateTime? OutstandingFrom, DateTime? DueDate);

    // requiredVersionPublishedDate: when the policy's RequiredVersionNumber was published, or null
    // when nothing has been published yet.
    public static Requirement Compute(Policy policy, PolicyAssignment assignment, DateTime? requiredVersionPublishedDate)
    {
        if (policy.RequiredVersionNumber <= 0 || requiredVersionPublishedDate is not DateTime requiredPublished)
        {
            return new Requirement(null, null);
        }

        bool acknowledgedRequired = assignment.AcknowledgedAtUtc is not null
            && assignment.AcknowledgedVersionNumber is int acknowledgedVersion
            && acknowledgedVersion >= policy.RequiredVersionNumber;

        if (!acknowledgedRequired)
        {
            // Due a set number of days after they could first have read it: when this version came
            // out, or when they joined the audience if that was later.
            DateTime from = Max(assignment.AssignedDate, requiredPublished);
            return new Requirement(from, from.AddDays(policy.AcknowledgeWithinDays));
        }

        if (policy.ReacknowledgeEveryMonths is not int months)
        {
            return new Requirement(null, null);
        }

        // Re-reading on a schedule: it expires N months after they last acknowledged, and shows
        // as "to read" for the usual number of days before that, so it can be done before it lapses.
        DateTime acknowledgedAt = assignment.AcknowledgedAtUtc!.Value;
        DateTime expires = acknowledgedAt.AddMonths(months);
        DateTime opens = Max(acknowledgedAt, expires.AddDays(-policy.AcknowledgeWithinDays));

        return new Requirement(opens, expires);
    }

    // Writes the computed dates onto the assignment. When the outstanding period starts afresh the
    // reminder count starts again too, so each new requirement gets its own reminders.
    public static bool Apply(Policy policy, PolicyAssignment assignment, DateTime? requiredVersionPublishedDate)
    {
        Requirement requirement = Compute(policy, assignment, requiredVersionPublishedDate);

        if (assignment.OutstandingFrom == requirement.OutstandingFrom && assignment.DueDate == requirement.DueDate)
        {
            return false;
        }

        if (assignment.OutstandingFrom != requirement.OutstandingFrom)
        {
            assignment.ReminderCount = 0;
            assignment.LastReminderSentDate = null;
        }

        assignment.OutstandingFrom = requirement.OutstandingFrom;
        assignment.DueDate = requirement.DueDate;

        return true;
    }

    public static PolicyStatus StatusAt(DateTime? outstandingFrom, DateTime? dueDate, DateTime nowUtc)
    {
        if (outstandingFrom is not DateTime from || from > nowUtc)
        {
            return PolicyStatus.UpToDate;
        }

        return dueDate is DateTime due && due < nowUtc ? PolicyStatus.Overdue : PolicyStatus.Outstanding;
    }

    public static PolicyStatus StatusAt(PolicyAssignment assignment, DateTime nowUtc) =>
        StatusAt(assignment.OutstandingFrom, assignment.DueDate, nowUtc);

    // The sentence ticked when acknowledging. Built on the server and stored with the record, so
    // what the page shows and what is kept as evidence can't drift apart.
    public static string StatementFor(string policyName, int versionNumber) =>
        $"I have read and understood {policyName} (version {versionNumber}).";

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
