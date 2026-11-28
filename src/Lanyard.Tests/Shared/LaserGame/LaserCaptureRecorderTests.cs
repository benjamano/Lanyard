using System.Net;
using System.Text;
using Lanyard.Shared.DTO;
using Lanyard.Shared.LaserGame.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserCaptureRecorderTests
{
    private static readonly IPAddress ControlBox = IPAddress.Parse("192.168.0.10");
    private static readonly IPAddress Broadcast = IPAddress.Parse("192.168.0.255");
    private static readonly DateTime T0 = new(2026, 11, 28, 15, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] GameStart = Convert.FromHexString("342c403031352c30");
    private static readonly byte[] PlayerScore = Convert.FromHexString("332c312c302c33302c312c33302c332c31302c30");
    private static readonly byte[] GameEnd = Convert.FromHexString("342c403031342c30");

    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    private ManualDelays _delays = null!;

    [TestInitialize]
    public void SetUp() => _delays = new ManualDelays();

    private LaserCaptureRecorder NewRecorder(Stream output, LaserCaptureStopMode mode = LaserCaptureStopMode.UntilStopped, long maxBytes = PacketCaptureRules.MaxRecordingBytes, int capacity = 10_000)
        => new(output, new LaserCaptureHeader { RecordedAtUtc = T0, Note = "test" }, new LaserCaptureRecordingOptions
        {
            Mode = mode,
            MaxDuration = MaxDuration,
            MaxBytes = maxBytes,
            ChannelCapacity = capacity,
        }, _delays.Delay);

    private static Task<LaserCapture> ReadAsync(MemoryStream output)
        => LaserCaptureReader.ReadAsync(new MemoryStream(output.ToArray()), CancellationToken.None);

    [TestMethod]
    public async Task RecordsPackets_WithOffsetsFromTheFirst_AndAFooter()
    {
        MemoryStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output);
        recorder.Start();

        Assert.IsTrue(recorder.TryRecord(T0, ControlBox, Broadcast, GameStart));
        Assert.IsTrue(recorder.TryRecord(T0.AddMilliseconds(1003), ControlBox, Broadcast, PlayerScore));
        Assert.IsTrue(recorder.TryRecord(T0.AddMilliseconds(2500), ControlBox, Broadcast, GameEnd));

        LaserCaptureRecordingResult result = await recorder.StopAsync();

        Assert.AreEqual(LaserCaptureStopReason.Stopped, result.Reason);
        Assert.AreEqual(3, result.Packets);
        Assert.AreEqual(0, result.Dropped);
        Assert.AreEqual(2500, result.DurationMs);
        Assert.IsNull(result.Error);

        LaserCapture capture = await ReadAsync(output);

        CollectionAssert.AreEqual(new long[] { 0, 1003, 2500 }, capture.Packets.Select(p => p.OffsetMs).ToArray());
        CollectionAssert.AreEqual(PlayerScore, capture.Packets[1].Payload);
        Assert.AreEqual("192.168.0.10", capture.Packets[0].SourceIp);
        Assert.AreEqual("192.168.0.255", capture.Packets[0].DestinationIp);
        Assert.AreEqual("test", capture.Header.Note);
        Assert.AreEqual(3, capture.Footer?.Packets);
    }

    [TestMethod]
    public async Task ATimestampSteppingBack_NeverMakesTimeGoBackwards()
    {
        MemoryStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output);
        recorder.Start();

        recorder.TryRecord(T0, null, null, GameStart);
        recorder.TryRecord(T0.AddMilliseconds(500), null, null, PlayerScore);
        recorder.TryRecord(T0.AddMilliseconds(499), null, null, PlayerScore);

        await recorder.StopAsync();

        CollectionAssert.AreEqual(new long[] { 0, 500, 500 }, (await ReadAsync(output)).Packets.Select(p => p.OffsetMs).ToArray());
    }

    [TestMethod]
    public async Task NextGame_StopsAfterTheGracePeriod_OnceTheGameEnds()
    {
        MemoryStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output, LaserCaptureStopMode.NextGame);
        recorder.Start();

        recorder.OnGameStarted();
        recorder.TryRecord(T0, null, null, GameStart);
        recorder.OnGameEnded();

        await _delays.WaitForAsync(PacketCaptureRules.NextGameGracePeriod);

        // Final score packets just after the end are still kept.
        Assert.IsTrue(recorder.TryRecord(T0.AddSeconds(2), null, null, PlayerScore));
        Assert.IsFalse(recorder.Completion.IsCompleted);

        _delays.Complete(PacketCaptureRules.NextGameGracePeriod);
        LaserCaptureRecordingResult result = await recorder.Completion;

        Assert.AreEqual(LaserCaptureStopReason.GameFinished, result.Reason);
        Assert.AreEqual(1, result.Games);
        Assert.AreEqual(2, result.Packets);
        Assert.AreEqual(1, (await ReadAsync(output)).Footer?.Games);
    }

    [TestMethod]
    public async Task NextGame_IgnoresTheEndOfAGameAlreadyRunningWhenRecordingStarted()
    {
        LaserCaptureRecorder recorder = NewRecorder(new MemoryStream(), LaserCaptureStopMode.NextGame);
        recorder.Start();

        recorder.OnGameEnded();

        Assert.IsFalse(_delays.Requested(PacketCaptureRules.NextGameGracePeriod));

        LaserCaptureRecordingResult result = await recorder.StopAsync();

        Assert.AreEqual(LaserCaptureStopReason.Stopped, result.Reason);
        Assert.AreEqual(0, result.Games);
    }

    [TestMethod]
    public async Task UntilStopped_KeepsGoingAcrossGames_AndCountsThem()
    {
        LaserCaptureRecorder recorder = NewRecorder(new MemoryStream());
        recorder.Start();

        recorder.OnGameStarted();
        recorder.OnGameEnded();
        recorder.OnGameStarted();
        recorder.OnGameEnded();

        Assert.IsFalse(recorder.IsStopping);

        LaserCaptureRecordingResult result = await recorder.StopAsync();

        Assert.AreEqual(2, result.Games);
    }

    [TestMethod]
    public async Task StopsAtTheTimeLimit()
    {
        LaserCaptureRecorder recorder = NewRecorder(new MemoryStream());
        recorder.Start();

        await _delays.WaitForAsync(MaxDuration);
        _delays.Complete(MaxDuration);

        Assert.AreEqual(LaserCaptureStopReason.MaxDuration, (await recorder.Completion).Reason);
    }

    [TestMethod]
    public async Task StopsAtTheSizeLimit_AndTheFileStillReadsBack()
    {
        MemoryStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output, maxBytes: 400);
        recorder.Start();

        for (int i = 0; i < 50; i++)
        {
            recorder.TryRecord(T0.AddMilliseconds(i * 100), ControlBox, Broadcast, PlayerScore);
        }

        LaserCaptureRecordingResult result = await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(LaserCaptureStopReason.MaxSize, result.Reason);
        Assert.IsLessThan(50L, result.Packets);

        LaserCapture capture = await ReadAsync(output);
        Assert.IsNotNull(capture.Footer);
        Assert.AreEqual(result.Packets, capture.Packets.Count);
    }

    [TestMethod]
    public async Task DropsPackets_RatherThanBlocking_WhenTheWriterFallsBehind()
    {
        GatedStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output, capacity: 2);
        recorder.Start();

        // The header write is held up, so nothing leaves the queue.
        await output.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        int accepted = 0;

        for (int i = 0; i < 10; i++)
        {
            if (recorder.TryRecord(T0.AddMilliseconds(i), null, null, PlayerScore))
            {
                accepted++;
            }
        }

        Assert.AreEqual(2, accepted);
        Assert.AreEqual(8, recorder.Dropped);

        output.Release();
        LaserCaptureRecordingResult result = await recorder.StopAsync();

        Assert.AreEqual(2, result.Packets);
        Assert.AreEqual(8, result.Dropped);
    }

    [TestMethod]
    public async Task AfterStopping_PacketsAreRefusedButNotCountedAsDropped()
    {
        LaserCaptureRecorder recorder = NewRecorder(new MemoryStream());
        recorder.Start();

        await recorder.StopAsync();

        Assert.IsFalse(recorder.TryRecord(T0, null, null, PlayerScore));
        Assert.AreEqual(0, recorder.Dropped);
    }

    [TestMethod]
    public async Task AFileThatCantBeWritten_EndsAsFailed_WithoutThrowing()
    {
        LaserCaptureRecorder recorder = NewRecorder(new BrokenStream());
        recorder.Start();

        LaserCaptureRecordingResult result = await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(LaserCaptureStopReason.Failed, result.Reason);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public void TryRecord_AllocatesLittleOnTheCaptureThread()
    {
        GatedStream output = new();
        LaserCaptureRecorder recorder = NewRecorder(output, capacity: 20_000);
        recorder.Start();
        output.FirstWriteStarted.Wait(TimeSpan.FromSeconds(10));

        // Warm up so the queue's first growth isn't counted against one packet.
        for (int i = 0; i < 100; i++)
        {
            recorder.TryRecord(T0, ControlBox, Broadcast, PlayerScore);
        }

        const int Packets = 5_000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Packets; i++)
        {
            recorder.TryRecord(T0.AddMilliseconds(i), ControlBox, Broadcast, PlayerScore);
        }

        long perPacket = (GC.GetAllocatedBytesForCurrentThread() - before) / Packets;

        output.Release();

        // A 20-byte payload copy is about 48 bytes; the rest is the queue growing now and then.
        Assert.IsLessThanOrEqualTo(256L, perPacket, $"{perPacket} bytes per packet");
    }

    /// <summary>Delays the test completes when it chooses, so time limits run without waiting.</summary>
    private sealed class ManualDelays
    {
        private readonly object _lock = new();
        private readonly List<(TimeSpan Wait, TaskCompletionSource Done)> _pending = [];

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetCanceled(token));

            lock (_lock)
            {
                _pending.Add((wait, done));
            }

            return done.Task;
        }

        public bool Requested(TimeSpan wait)
        {
            lock (_lock)
            {
                return _pending.Any(p => p.Wait == wait);
            }
        }

        public async Task WaitForAsync(TimeSpan wait)
        {
            for (int i = 0; i < 500 && !Requested(wait); i++)
            {
                await Task.Delay(10);
            }

            Assert.IsTrue(Requested(wait), $"Nothing waited for {wait}.");
        }

        public void Complete(TimeSpan wait)
        {
            lock (_lock)
            {
                foreach ((TimeSpan _, TaskCompletionSource done) in _pending.Where(p => p.Wait == wait))
                {
                    done.TrySetResult();
                }
            }
        }
    }

    /// <summary>Holds up writes until released, to make the writer fall behind on purpose.</summary>
    private sealed class GatedStream : MemoryStream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWriteStarted => _firstWrite.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _firstWrite.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("Disk full");
    }
}
