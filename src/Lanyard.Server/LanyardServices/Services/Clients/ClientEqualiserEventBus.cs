namespace Lanyard.Application.Services.Clients;

// Tells open pages that a kiosk's music equaliser was changed, so every EQ button showing that
// kiosk (the Music page, Now playing widgets on dashboards left open) re-reads its preset instead
// of keeping the one it loaded. Published only by ClientService.SetClientEqualiserAsync, the one
// writer of the EQ columns. Same in-process pattern as IKioskFleetEventBus.
public interface IClientEqualiserEventBus
{
    event Action<Guid>? OnChanged;

    void Publish(Guid clientId);
}

public class ClientEqualiserEventBus : IClientEqualiserEventBus
{
    public event Action<Guid>? OnChanged;

    public void Publish(Guid clientId) => OnChanged?.Invoke(clientId);
}
