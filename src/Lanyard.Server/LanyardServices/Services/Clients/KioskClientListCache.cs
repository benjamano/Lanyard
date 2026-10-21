using System.Collections.Concurrent;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Clients;

// The kiosk list behind the Clients page, the Music page and the Kiosk Health widget, held per
// company so every open tab shares one read. Singleton. Entries are dropped as soon as anything
// about a company's kiosks is published on IKioskFleetEventBus, and expire after Ttl in case a
// write path doesn't publish.
//
// Only database facts are held. Whether a kiosk is connected comes from the hub's live connection
// set, so KioskClientListService works that out again on every read.
public interface IKioskClientListCache
{
    // Incremented on every eviction of the company. A load takes it before reading the database
    // and hands it back to TrySet, so a load that started before an eviction can't put the old
    // rows back.
    long GetVersion(int companyId);

    bool TryGet(int companyId, out IReadOnlyList<ClientConnectedWithCapabilitiesDTO> clients);

    void TrySet(int companyId, long version, IReadOnlyList<ClientConnectedWithCapabilitiesDTO> clients);

    void Evict(int companyId);
}

public sealed class KioskClientListCache : IKioskClientListCache, IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private sealed record Entry(long Version, DateTime ExpiresUtc, IReadOnlyList<ClientConnectedWithCapabilitiesDTO> Clients);

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly ConcurrentDictionary<int, long> _versions = new();
    private readonly IKioskFleetEventBus _fleetEvents;
    private readonly TimeProvider _timeProvider;

    public KioskClientListCache(IKioskFleetEventBus fleetEvents, TimeProvider timeProvider)
    {
        _fleetEvents = fleetEvents;
        _timeProvider = timeProvider;

        _fleetEvents.OnChanged += Evict;
    }

    public long GetVersion(int companyId) => _versions.GetValueOrDefault(companyId);

    public bool TryGet(int companyId, out IReadOnlyList<ClientConnectedWithCapabilitiesDTO> clients)
    {
        if (_entries.TryGetValue(companyId, out Entry? entry)
            && entry.Version == GetVersion(companyId)
            && entry.ExpiresUtc > _timeProvider.GetUtcNow().UtcDateTime)
        {
            clients = entry.Clients;
            return true;
        }

        clients = [];
        return false;
    }

    public void TrySet(int companyId, long version, IReadOnlyList<ClientConnectedWithCapabilitiesDTO> clients)
    {
        if (version != GetVersion(companyId))
        {
            return;
        }

        _entries[companyId] = new Entry(version, _timeProvider.GetUtcNow().UtcDateTime + Ttl, clients);
    }

    public void Evict(int companyId)
    {
        _versions.AddOrUpdate(companyId, 1, (_, version) => version + 1);
        _entries.TryRemove(companyId, out _);
    }

    public void Dispose()
    {
        _fleetEvents.OnChanged -= Evict;
    }
}
