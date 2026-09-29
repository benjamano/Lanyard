namespace Lanyard.Application.Services.Scheduling;

// Tells open pages that time off changed at a location - a request made, cancelled or decided - so
// the managers' "waiting for approval" count in the nav updates without a reload. Same in-process
// pattern as ITerminalEventBus (and the same single-app-instance assumption).
public interface ITimeOffEventBus
{
    event Action<int>? OnChanged;

    void Publish(int locationId);
}

public class TimeOffEventBus : ITimeOffEventBus
{
    public event Action<int>? OnChanged;

    public void Publish(int locationId) => OnChanged?.Invoke(locationId);
}
