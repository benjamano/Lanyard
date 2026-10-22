using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.DependencyInjection;

public class SignalRProjectionControlHubEvents(IServiceScopeFactory serviceScopeFactory)
{
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    // Process-wide, so every open Clients page hears every kiosk's report. Each report carries the
    // kiosk it came from so a page only shows its own kiosk's songs, and names are resolved within
    // that kiosk's company - the scope here isn't tied to a signed-in user (Security Issue 9.12).
    public event Action<Guid, Result<IEnumerable<CachedSongDTO>>>? OnReceiveCachedSongs;

    public async Task RaiseReceiveCachedSongs(Guid clientId, int companyId, Result<IEnumerable<CachedSongDTO>> result)
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        IMusicService musicService = scope.ServiceProvider.GetRequiredService<IMusicService>();

        // A kiosk can report hundreds of cached songs; resolve every name with one query
        // instead of one tracked Song load per entry.
        List<CachedSongDTO> cachedSongs = (result.Data ?? Enumerable.Empty<CachedSongDTO>()).ToList();

        Result<IReadOnlyDictionary<Guid, string>> namesResult = await musicService.GetSongNamesAsync(cachedSongs.Select(x => x.Id).Distinct().ToList(), companyId);
        IReadOnlyDictionary<Guid, string> names = namesResult.Data ?? new Dictionary<Guid, string>();

        foreach (CachedSongDTO cachedSong in cachedSongs)
        {
            cachedSong.Name = names.TryGetValue(cachedSong.Id, out string? name) ? name : string.Empty;
        }

        OnReceiveCachedSongs?.Invoke(clientId, result);
    }
}