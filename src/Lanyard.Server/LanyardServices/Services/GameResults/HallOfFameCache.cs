using System.Collections.Concurrent;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Lanyard.Application.Services;

// Singleton. Every Hall of Fame widget in every circuit (kiosk displays, staff home, the editor)
// reads through here, so an open dashboard costs nothing between games instead of three queries a
// minute each. Leaderboards only change when a game is recorded, which invalidates the company.
public sealed class HallOfFameCache(IMemoryCache cache, ILogger<HallOfFameCache> logger) : IHallOfFameCache
{
    // Only bounds staleness from writes this process never saw (a manual DB edit, another
    // instance). Normal changes arrive through Invalidate.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<HallOfFameCache> _logger = logger;

    // One token per company, attached to each of its entries, so Invalidate drops that company's
    // whole set (every kiosk, period and size) without having to know the keys.
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _companyTokens = new();

    // Makes the look-up-or-create step atomic, so a burst of misses (every widget reloading after
    // a game ends) shares one load instead of each running its own queries.
    private readonly Lock _gate = new();

    public event Action<int>? Changed;

    public async Task<HallOfFameSnapshotDTO> GetOrLoadAsync(int companyId, Guid? clientId, DateTime? fromUtc, int take, Func<Task<HallOfFameSnapshotDTO>> load)
    {
        // The window's start is in the key rather than the period, so "Today" becomes a new entry
        // at midnight instead of serving yesterday's rows until the next game.
        string key = $"hall-of-fame:{companyId}:{clientId?.ToString() ?? "all"}:{fromUtc?.Ticks.ToString() ?? "all"}:{take}";

        Lazy<Task<HallOfFameSnapshotDTO>> entry;

        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out entry!))
            {
                entry = new Lazy<Task<HallOfFameSnapshotDTO>>(load);

                CancellationTokenSource companyToken = _companyTokens.GetOrAdd(companyId, _ => new CancellationTokenSource());

                MemoryCacheEntryOptions options = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(CacheTtl)
                    .AddExpirationToken(new CancellationChangeToken(companyToken.Token));

                _cache.Set(key, entry, options);
            }
        }

        try
        {
            return await entry.Value;
        }
        catch
        {
            // Don't hold on to a failure: the next caller should get a fresh attempt, not the
            // same exception for the next ten minutes.
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out Lazy<Task<HallOfFameSnapshotDTO>>? current) && ReferenceEquals(current, entry))
                {
                    _cache.Remove(key);
                }
            }

            throw;
        }
    }

    public void Invalidate(int companyId)
    {
        // Not disposed: a reader may have fetched this source a moment ago and still be about to
        // read its Token. Without a timer a CancellationTokenSource holds nothing that needs it.
        if (_companyTokens.TryRemove(companyId, out CancellationTokenSource? companyToken))
        {
            companyToken.Cancel();
        }

        _logger.LogInformation("Hall of Fame cache invalidated for company {CompanyId}", companyId);

        Changed?.Invoke(companyId);
    }
}
