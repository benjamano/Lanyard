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

public sealed class SpeechPreviewStore : ISpeechPreviewStore, IDisposable
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    // Sized in bytes, so previews can't pile up: past this the oldest are dropped, and a browser
    // that asks for one of those just gets "not found" and the person presses Preview again.
    private const long CacheBytes = 32L * 1024 * 1024;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = CacheBytes });

    private sealed record Entry(byte[] Wav, string UserId);

    public Guid Add(byte[] wav, string userId)
    {
        Guid id = Guid.NewGuid();
        MemoryCacheEntryOptions options = new() { AbsoluteExpirationRelativeToNow = KeepFor, Size = wav.Length };

        cache.Set(Key(id), new Entry(wav, userId), options);

        // A full size-limited cache turns a new entry away rather than making room, so the newest
        // preview would be the one lost. Drop the oldest half and try again.
        if (!cache.TryGetValue(Key(id), out _))
        {
            cache.Compact(0.5);
            cache.Set(Key(id), new Entry(wav, userId), options);
        }

        return id;
    }

    // Another user's id reads as not found, the same as an expired one.
    public byte[]? Get(Guid id, string userId) =>
        cache.TryGetValue(Key(id), out Entry? entry) && entry is not null && string.Equals(entry.UserId, userId, StringComparison.Ordinal)
            ? entry.Wav
            : null;

    public void Dispose() => cache.Dispose();

    private static string Key(Guid id) => $"speech-preview:{id:N}";
}
