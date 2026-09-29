using Lanyard.Application.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Music;

[TestClass]
public class SongAnalysisQueueTests
{
    private static async Task<List<Guid>> DrainAsync(SongAnalysisQueue queue, int expected)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        List<Guid> items = [];

        try
        {
            await foreach (Guid id in queue.DequeueAllAsync(cts.Token))
            {
                items.Add(id);

                if (items.Count == expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing more arrived within the window.
        }

        return items;
    }

    [TestMethod]
    public async Task Enqueue_SameIdWhileStillQueued_IsOnlyDeliveredOnce()
    {
        SongAnalysisQueue queue = new();
        Guid song = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        // The five-minute backfill sweep re-enqueues every pending song while a backlog drains.
        queue.Enqueue(song);
        queue.Enqueue(song);
        queue.Enqueue(other);
        queue.Enqueue(song);

        List<Guid> delivered = await DrainAsync(queue, expected: 2);

        CollectionAssert.AreEqual(new List<Guid> { song, other }, delivered);
    }

    [TestMethod]
    public async Task Enqueue_AfterTheIdWasDequeued_CanBeQueuedAgain()
    {
        SongAnalysisQueue queue = new();
        Guid song = Guid.NewGuid();

        queue.Enqueue(song);
        List<Guid> first = await DrainAsync(queue, expected: 1);

        queue.Enqueue(song);
        List<Guid> second = await DrainAsync(queue, expected: 1);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
    }
}
