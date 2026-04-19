#nullable enable

using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

public interface IActionExecutor
{
    bool CanHandle(string actionType);
    Task<(bool Success, string? ErrorMessage)> ExecuteAsync(AutomationRuleAction action, Guid triggerClientId);
}
