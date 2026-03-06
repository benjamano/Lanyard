using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

public interface IFlowAutomationService
{
    Task<Result<bool>> HandleTriggerAsync(FlowTriggerEvent trigger, CancellationToken ct = default);

    Task<Result<IEnumerable<AutomationFlow>>> GetFlowsAsync(
        string? triggerKey = null,
        Guid? clientId = null,
        bool activeOnly = true,
        CancellationToken ct = default);

    Task<Result<AutomationFlow>> SaveFlowAsync(AutomationFlow flow, CancellationToken ct = default);
}
