#nullable enable

using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

public interface IAutomationLogService
{
    Task<Result<IEnumerable<AutomationRuleExecution>>> GetRecentExecutionsAsync(int count);
}
