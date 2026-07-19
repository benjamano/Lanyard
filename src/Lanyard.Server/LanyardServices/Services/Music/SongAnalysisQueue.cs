using System.Threading.Channels;

namespace Lanyard.Application.Services;

public class SongAnalysisQueue : ISongAnalysisQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid songId)
    {
        // Unbounded channel: TryWrite only fails once the writer is completed,
        // which never happens for this singleton's lifetime.
        _channel.Writer.TryWrite(songId);
    }

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
