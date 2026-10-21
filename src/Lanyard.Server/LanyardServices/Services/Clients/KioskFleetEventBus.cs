namespace Lanyard.Application.Services.Clients;

// Tells open pages that something shown about a company's kiosks changed - one connected or
// disconnected, was renamed, paired, moved location, or had its capabilities or update ring
// changed - so the Clients page refreshes without polling, and the shared client list cache drops
// that company's entry. Heartbeats don't publish: they arrive every 30 s per kiosk, and health that
// only changes with time is picked up by the page's slow fallback poll instead. Same in-process
// pattern as IShiftClaimEventBus.
public interface IKioskFleetEventBus
{
    event Action<int>? OnChanged;

    void Publish(int companyId);
}

public class KioskFleetEventBus : IKioskFleetEventBus
{
    public event Action<int>? OnChanged;

    public void Publish(int companyId) => OnChanged?.Invoke(companyId);
}
