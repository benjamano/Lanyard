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

        foreach (CachedSongDTO cachedSong in result.Data ?? Enumerable.Empty<CachedSongDTO>())
        {
            Result<Song> songResult = await musicService.GetSongAsync(cachedSong.Id);

            cachedSong.Name = songResult.Data?.Name ?? string.Empty;
        }

        OnReceiveCachedSongs?.Invoke(result);
    }
}