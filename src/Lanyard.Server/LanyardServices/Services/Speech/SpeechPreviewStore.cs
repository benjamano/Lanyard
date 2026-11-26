using Microsoft.Extensions.Caching.Memory;

namespace Lanyard.Application.Services.Speech;

/// <summary>
/// Holds a rendered announcement for a few minutes so the browser that asked for a preview can
/// play it from a URL. Only the person who asked can fetch it, and it's never written anywhere.
/// </summary>
public interface ISpeechPreviewStore
{
    Guid Add(byte[] wav, string userId);

    byte[]? Get(Guid id, string userId);
}

public sealed class SpeechPreviewStore(IMemoryCache cache) : ISpeechPreviewStore
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private sealed record Entry(byte[] Wav, string UserId);

    public Guid Add(byte[] wav, string userId)
    {
        Guid id = Guid.NewGuid();
        cache.Set(Key(id), new Entry(wav, userId), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = KeepFor, Size = 1 });
        return id;
    }

    // Another user's id reads as not found, the same as an expired one.
    public byte[]? Get(Guid id, string userId) =>
        cache.TryGetValue(Key(id), out Entry? entry) && entry is not null && string.Equals(entry.UserId, userId, StringComparison.Ordinal)
            ? entry.Wav
            : null;

    private static string Key(Guid id) => $"speech-preview:{id:N}";
}
