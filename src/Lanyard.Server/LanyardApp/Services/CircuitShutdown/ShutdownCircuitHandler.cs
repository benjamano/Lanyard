using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Lanyard.App.Services.CircuitShutdown;

// One per circuit. Keeps the coordinator's list of open circuits in step with live connections.
public sealed class ShutdownCircuitHandler(ICircuitShutdownCoordinator coordinator) : CircuitHandler
{
    private readonly ICircuitShutdownCoordinator _coordinator = coordinator;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _coordinator.Register(circuit.Id, ct => circuit.RequestCircuitPauseAsync(ct));
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _coordinator.Unregister(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _coordinator.Unregister(circuit.Id);
        return Task.CompletedTask;
    }
}
