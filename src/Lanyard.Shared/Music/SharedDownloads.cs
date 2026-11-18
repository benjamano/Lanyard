using System.Collections.Concurrent;

namespace Lanyard.Shared.Music;

/// <summary>
/// One download per song at a time, shared by everyone who wants it. A song asked for while it's
/// already downloading (say, pre-cached as "up next" and then skipped to) joins that download
/// instead of starting a second one, or waiting behind every other download.
/// </summary>
public sealed class SharedDownloads<TResult>
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<TResult>>> _running = new();

    public int RunningCount => _running.Count;

    /// <summary>
    /// The download for <paramref name="songId"/>, started with <paramref name="start"/> if none is
    /// running. <paramref name="cancellationToken"/> only stops this caller waiting; the download
    /// carries on for anyone else (and for the cache).
    /// </summary>
    public Task<TResult> GetOrStartAsync(Guid songId, Func<Task<TResult>> start, CancellationToken cancellationToken = default)
    {
        Lazy<Task<TResult>> download = _running.GetOrAdd(songId, id => new Lazy<Task<TResult>>(() => RunAsync(id, start)));

        return cancellationToken.CanBeCanceled ? download.Value.WaitAsync(cancellationToken) : download.Value;
    }

    private async Task<TResult> RunAsync(Guid songId, Func<Task<TResult>> start)
    {
        try
        {
            // Off the caller's thread, so a caller that stops waiting doesn't take the work with it.
            return await Task.Run(start);
        }
        finally
        {
            _running.TryRemove(songId, out _);
        }
    }
}
