using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// In-process fan-out of clock events to open terminal pages, the same pattern the projection and
// automation runners use for live UI (components subscribe over their own Blazor circuit). Needed
// because a QR clock-in happens on the person's phone, but the greeting belongs on the tablet.
public interface ITerminalEventBus
{
    event Action<TerminalClockEvent>? OnClock;

    void Publish(TerminalClockEvent clockEvent);
}

public class TerminalEventBus : ITerminalEventBus
{
    public event Action<TerminalClockEvent>? OnClock;

    public void Publish(TerminalClockEvent clockEvent) => OnClock?.Invoke(clockEvent);
}
