#nullable enable

using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

public interface IAutomationRuleService
{
    Task<Result<IEnumerable<AutomationRule>>> GetRulesAsync();
    Task<Result<AutomationRule?>> GetRuleAsync(Guid id);
    Task<Result<IEnumerable<AutomationRule>>> GetRulesByTriggerAsync(Guid triggerClientId, GameStatus triggerEvent);
    Task<Result<AutomationRule>> CreateRuleAsync(AutomationRule rule);
    Task<Result<AutomationRule>> UpdateRuleAsync(AutomationRule rule);
    Task<Result<bool>> DeleteRuleAsync(Guid id);
    Task<Result<AutomationRule>> SetRuleEnabledAsync(Guid id, bool isEnabled);

    /// <summary>
    /// Copies a rule with its conditions and actions as a new, switched-off rule named
    /// "Copy of ...". Fails when the rule doesn't exist (or is another company's), or when the copy
    /// wouldn't pass the same checks as a save, for example because its kiosk has been removed.
    /// </summary>
    Task<Result<AutomationRule>> DuplicateRuleAsync(Guid id);

    /// <summary>
    /// The rule's active actions in order, each described in plain words with the names of its kiosk
    /// and of the playlist, program, scene or clip it plays.
    /// </summary>
    Task<Result<List<AutomationActionDescription>>> DescribeActionsAsync(Guid ruleId);

    /// <summary>
    /// "Run now (test)" for the signed-in user: runs the rule's actions once against its trigger
    /// kiosk and logs it as a test by them. The rule is found through the caller's company, so
    /// another company's rule is "not found". See <see cref="IAutomationEngineService.TestRunRuleAsync"/>.
    /// </summary>
    Task<Result<AutomationTestRunResult>> TestRunRuleAsync(Guid ruleId, bool checkConditions);
}
