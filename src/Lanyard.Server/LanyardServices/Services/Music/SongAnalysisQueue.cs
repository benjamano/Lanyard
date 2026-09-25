using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Lanyard.Application.Services;

public class SongAnalysisQueue : ISongAnalysisQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    // Ids currently waiting in the channel. The backfill sweep re-enqueues every NotAnalyzed
    // song every five minutes; while a backlog drains one song at a time that used to add a
    // second, third, ... copy of every pending id, each costing a scope and a query to skip.
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    public void Enqueue(Guid songId)
    {
        if (!_queued.TryAdd(songId, 0))
        {
            return;
        }

        // Unbounded channel: TryWrite only fails once the writer is completed,
        // which never happens for this singleton's lifetime.
        _channel.Writer.TryWrite(songId);
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Guid songId in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _queued.TryRemove(songId, out _);
            yield return songId;
        }
    }
}
