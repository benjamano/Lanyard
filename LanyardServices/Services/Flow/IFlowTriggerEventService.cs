using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface IFlowTriggerEventService
{
    Task EmitAsync(FlowTriggerEvent triggerEvent, CancellationToken cancellationToken = default);
}
