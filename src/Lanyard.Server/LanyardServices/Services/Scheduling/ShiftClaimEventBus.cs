namespace Lanyard.Application.Services.Scheduling;

// Tells open pages that shift requests changed at a location - a pick-up, call-off or swap made,
// withdrawn or decided, or a shift opened up - so My Shifts and the managers' nav count update
// without a reload. Same in-process pattern as ITimeOffEventBus.
public interface IShiftClaimEventBus
{
    event Action<int>? OnChanged;

    void Publish(int locationId);
}

public class ShiftClaimEventBus : IShiftClaimEventBus
{
    public event Action<int>? OnChanged;

    public void Publish(int locationId) => OnChanged?.Invoke(locationId);
}
