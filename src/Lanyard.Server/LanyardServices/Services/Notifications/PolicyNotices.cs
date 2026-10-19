using Lanyard.Infrastructure.DTO.Notifications;

namespace Lanyard.Application.Services.Notifications;

// Wording for policy reminders, shared by the email and the push.
public static class PolicyNotices
{
    public static string Url(Guid policyId) => $"/policies/{policyId}";

    public static Notice For(PolicyReminderPayload payload) => payload.Kind switch
    {
        PolicyReminderKind.Overdue => new Notice(
            $"{payload.PolicyName} is overdue",
            [$"You were asked to read and acknowledge {payload.PolicyName} by {payload.DueDate}.", "Open it, read it, and tick to say you've read it."],
            Url(payload.PolicyId), "Read the policy", Tag(payload.PolicyId)),

        PolicyReminderKind.ReadAgain => new Notice(
            $"Please read {payload.PolicyName} again",
            [$"Version {payload.VersionNumber} of {payload.PolicyName} needs you to read and acknowledge it again.", $"Please do it by {payload.DueDate}."],
            Url(payload.PolicyId), "Read the policy", Tag(payload.PolicyId)),

        _ => new Notice(
            $"Please read {payload.PolicyName}",
            [$"You've been asked to read and acknowledge {payload.PolicyName}.", $"Please do it by {payload.DueDate}."],
            Url(payload.PolicyId), "Read the policy", Tag(payload.PolicyId))
    };

    private static string Tag(Guid policyId) => $"policy-{policyId:N}";
}
