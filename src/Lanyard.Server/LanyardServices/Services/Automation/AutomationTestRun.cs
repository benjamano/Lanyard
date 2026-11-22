#nullable enable

namespace Lanyard.Application.Services;

/// <param name="RuleId">The rule to run.</param>
/// <param name="CallerCompanyId">The signed-in user's company; a rule from any other is "not found".</param>
/// <param name="UserId">Who is running the test (AspNetUsers.Id), stored on the log row.</param>
/// <param name="UserDisplayName">Shown in the log as "Manual test by ...".</param>
/// <param name="CheckConditions">Check the rule's "only if" conditions first, as a real trigger would.</param>
public sealed record AutomationTestRunRequest(
    Guid RuleId,
    int CallerCompanyId,
    string UserId,
    string UserDisplayName,
    bool CheckConditions);

public sealed record AutomationTestActionResult(
    Guid ActionId,
    string ActionType,
    string ParametersJson,
    bool Success,
    string? ErrorMessage);

/// <param name="ExecutionId">The execution log row written for this test.</param>
/// <param name="SkipReason">Set when CheckConditions was on and a condition didn't hold; nothing ran.</param>
public sealed record AutomationTestRunResult(
    Guid ExecutionId,
    string? SkipReason,
    IReadOnlyList<AutomationTestActionResult> Actions)
{
    public bool Skipped => SkipReason != null;

    public bool AllPassed => !Skipped && Actions.All(a => a.Success);
}
