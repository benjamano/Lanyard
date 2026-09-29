using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.DependencyInjection;

public class SignalRProjectionControlHubEvents(IServiceScopeFactory serviceScopeFactory)
{
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    public event Action<Result<IEnumerable<CachedSongDTO>>>? OnReceiveCachedSongs;

    public async Task RaiseReceiveCachedSongs(Result<IEnumerable<CachedSongDTO>> result)
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
        IMusicService musicService = scope.ServiceProvider.GetRequiredService<IMusicService>();

        // A kiosk can report hundreds of cached songs; resolve every name with one query
        // instead of one tracked Song load per entry.
        List<CachedSongDTO> cachedSongs = (result.Data ?? Enumerable.Empty<CachedSongDTO>()).ToList();

        Result<IReadOnlyDictionary<Guid, string>> namesResult = await musicService.GetSongNamesAsync(cachedSongs.Select(x => x.Id).Distinct().ToList());
        IReadOnlyDictionary<Guid, string> names = namesResult.Data ?? new Dictionary<Guid, string>();

        foreach (CachedSongDTO cachedSong in cachedSongs)
        {
            cachedSong.Name = names.TryGetValue(cachedSong.Id, out string? name) ? name : string.Empty;
        }

        OnReceiveCachedSongs?.Invoke(result);
    }
}