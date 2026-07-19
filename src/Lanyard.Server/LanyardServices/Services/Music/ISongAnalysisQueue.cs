namespace Lanyard.Application.Services;

/// <summary>
/// Hands song IDs to the background BPM analysis worker. Producers (file uploads,
/// the startup backfill) enqueue; <see cref="SongAnalysisHostedService"/> is the
/// single consumer.
/// </summary>
public interface ISongAnalysisQueue
{
    void Enqueue(Guid songId);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
