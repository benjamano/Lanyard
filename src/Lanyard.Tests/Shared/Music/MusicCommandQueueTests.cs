using Lanyard.Shared.Music;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

namespace Lanyard.Tests.Shared.Music;

[TestClass]
public class MusicCommandQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Commands_RunOneAtATime_InTheOrderTheyArrived()
    {
        await using MusicCommandQueue queue = new();
        ConcurrentQueue<string> ran = new();
        int running = 0;
        bool overlapped = false;

        foreach (string name in new[] { "load", "play", "pause", "seek" })
        {
            queue.Enqueue(name, async _ =>
            {
                if (Interlocked.Increment(ref running) > 1)
                {
                    overlapped = true;
                }

                await Task.Delay(20);
                ran.Enqueue(name);
                Interlocked.Decrement(ref running);
            });
        }

        await queue.DrainAsync().WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { "load", "play", "pause", "seek" }, ran.ToArray());
        Assert.IsFalse(overlapped);
    }

    [TestMethod]
    public async Task Enqueueing_ReturnsStraightAway_WhileASlowLoadRuns()
    {
        await using MusicCommandQueue queue = new();
        TaskCompletionSource release = new();

        queue.EnqueueLoad("slow download", async _ => await release.Task);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        queue.Enqueue("pause", _ => Task.CompletedTask);
        watch.Stop();

        Assert.IsTrue(watch.ElapsedMilliseconds < 500, "Adding a command must not wait for the one running.");

        release.SetResult();
        await queue.DrainAsync().WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task ANewLoad_CancelsTheRunningOne_AndSkipsOlderQueuedLoads()
    {
        await using MusicCommandQueue queue = new();
        ConcurrentQueue<string> ran = new();
        ConcurrentQueue<string> superseded = new();
        queue.Superseded += superseded.Enqueue;

        TaskCompletionSource firstStarted = new();
        bool firstCancelled = false;

        queue.EnqueueLoad("first", async ct =>
        {
            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout, ct);
            }
            catch (OperationCanceledException)
            {
                firstCancelled = true;
                throw;
            }
        });

        await firstStarted.Task.WaitAsync(Timeout);

        queue.EnqueueLoad("second", _ => { ran.Enqueue("second"); return Task.CompletedTask; });
        queue.Enqueue("play", _ => { ran.Enqueue("play"); return Task.CompletedTask; });
        queue.EnqueueLoad("third", _ => { ran.Enqueue("third"); return Task.CompletedTask; });

        await queue.DrainAsync().WaitAsync(Timeout);

        Assert.IsTrue(firstCancelled, "The download that's running is abandoned for the newer load.");
        CollectionAssert.AreEqual(new[] { "play", "third" }, ran.ToArray(),
            "The second load is skipped (the third replaces it); play still runs, in order.");
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, superseded.ToArray());
    }

    [TestMethod]
    public async Task AFailingCommand_DoesNotStopTheOnesAfterIt()
    {
        List<string> errors = [];
        await using MusicCommandQueue queue = new((name, _) => errors.Add(name));
        bool ranAfter = false;

        queue.Enqueue("broken", _ => throw new InvalidOperationException("boom"));
        queue.Enqueue("after", _ => { ranAfter = true; return Task.CompletedTask; });

        await queue.DrainAsync().WaitAsync(Timeout);

        Assert.IsTrue(ranAfter);
        CollectionAssert.AreEqual(new[] { "broken" }, errors);
    }
}
