using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class FlowTriggerEventService(ILogger<FlowTriggerEventService> logger) : IFlowTriggerEventService
{
    private readonly ILogger<FlowTriggerEventService> _logger = logger;

    public Task EmitAsync(FlowTriggerEvent triggerEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Emitting flow trigger event {EventKey} for client {ClientId} at {OccurredUtc}",
            triggerEvent.EventKey,
            triggerEvent.ClientId,
            triggerEvent.OccurredUtc);

        return Task.CompletedTask;
    }
}
