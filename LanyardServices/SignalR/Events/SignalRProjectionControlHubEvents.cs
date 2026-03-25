using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

public class SignalRProjectionControlHubEvents
{
    public event Action<Result<IEnumerable<CachedSongDTO>>>? OnReceiveCachedSongs;

    public void RaiseReceiveCachedSongs(Result<IEnumerable<CachedSongDTO>> result)
    {
        OnReceiveCachedSongs?.Invoke(result);
    }
}