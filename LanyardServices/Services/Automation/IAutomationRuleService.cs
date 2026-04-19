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
}
