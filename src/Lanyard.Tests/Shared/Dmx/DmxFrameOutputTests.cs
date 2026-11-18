using Lanyard.Shared.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Dmx;

[TestClass]
public class DmxFrameOutputTests
{
    private sealed class FakePort : IDmxPort
    {
        public List<uint> Opens { get; } = [];
        public int Closes { get; private set; }
        public List<byte[]> Frames { get; } = [];
        public string? OpenError { get; set; }
        public string? WriteError { get; set; }

        public string? Open(uint deviceIndex)
        {
            Opens.Add(deviceIndex);
            return OpenError;
        }

        public string? SendBreak() => null;

        public string? Write(byte[] frame)
        {
            lock (Frames)
            {
                Frames.Add((byte[])frame.Clone());
            }

            return WriteError;
        }

        public void Close() => Closes++;

        public byte[] LastFrame
        {
            get
            {
                lock (Frames)
                {
                    return Frames[^1];
                }
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // One port shared across opens, so tests can see every open/close/frame in order.
    private static (DmxFrameOutput Output, FakePort Port, ManualTime Time) Build()
    {
        FakePort port = new();
        ManualTime time = new();
        DmxFrameOutput output = new(() => port, time: time, runFrameThread: false);

        return (output, port, time);
    }

    [TestMethod]
    public void Open_SameIndexAgain_DoesNotReopen()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();

        Assert.IsTrue(output.Open(0));
        Assert.IsTrue(output.Open(0));
        Assert.IsTrue(output.Open(0));

        CollectionAssert.AreEqual(new uint[] { 0 }, port.Opens, "Every SignalR reconnect re-sends the settings; that must not re-open the device.");
        Assert.AreEqual(0, port.Closes);
    }

    [TestMethod]
    public void Open_DifferentIndex_ClosesTheOldOneFirst_WithoutBlackingOut()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();

        output.Open(0);
        output.SetChannel(1, 200);
        output.Open(1);

        CollectionAssert.AreEqual(new uint[] { 0, 1 }, port.Opens);
        Assert.AreEqual(1, port.Closes);
        Assert.AreEqual(0, port.Frames.Count, "Switching interface keeps the levels; only Close blacks out.");

        output.SendFrameNow();
        Assert.AreEqual((byte)200, port.LastFrame[1]);
    }

    [TestMethod]
    public void SetChannels_OutOfRange_AreSkippedAndTheRestApplied()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();
        output.Open(0);

        int skipped = output.SetChannels([(0, 9), (1, 10), (513, 9), (512, 20), (-1, 9)]);
        output.SendFrameNow();

        Assert.AreEqual(3, skipped);
        Assert.AreEqual(513, port.LastFrame.Length);
        Assert.AreEqual((byte)0, port.LastFrame[0], "Start code");
        Assert.AreEqual((byte)10, port.LastFrame[1]);
        Assert.AreEqual((byte)20, port.LastFrame[512]);
    }

    [TestMethod]
    public void SetChannel_OutOfRange_DoesNotThrow()
    {
        (DmxFrameOutput output, _, _) = Build();

        output.SetChannel(0, 1);
        output.SetChannel(600, 1);
    }

    [TestMethod]
    public void SetUniverse_ReplacesEveryChannel()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();
        output.Open(0);

        byte[] universe = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        output.SetUniverse(universe);
        output.SendFrameNow();

        CollectionAssert.AreEqual(universe, port.LastFrame[1..]);
    }

    [TestMethod]
    public void ValuesSetWhileClosed_GoOutOnceTheDeviceOpens()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();

        output.SetChannel(7, 77);
        output.Open(0);
        output.SendFrameNow();

        Assert.AreEqual((byte)77, port.LastFrame[7]);
    }

    [TestMethod]
    public void RepeatedWriteFailures_CloseThePort_ThenItReopensAfterTheRetryInterval()
    {
        (DmxFrameOutput output, FakePort port, ManualTime time) = Build();
        output.Open(0);
        port.WriteError = "status FT_IO_ERROR";

        for (int i = 0; i < DmxFrameOutput.FailuresBeforeReopen; i++)
        {
            output.SendFrameNow();
        }

        Assert.IsFalse(output.IsOpen);
        Assert.AreEqual(1, port.Closes);
        Assert.AreEqual(0, output.FramesWritten);

        // Unplugged: nothing is tried until the retry interval passes.
        port.WriteError = null;
        output.SendFrameNow();
        Assert.AreEqual(1, port.Opens.Count);

        time.Now += DmxFrameOutput.ReopenInterval;
        output.SendFrameNow();

        Assert.IsTrue(output.IsOpen);
        Assert.AreEqual(2, port.Opens.Count);
        Assert.AreEqual(1, output.FramesWritten);
        Assert.IsNull(output.LastWriteError);
    }

    [TestMethod]
    public void OpenFailure_IsRetriedInTheBackground()
    {
        (DmxFrameOutput output, FakePort port, ManualTime time) = Build();
        port.OpenError = "status FT_DEVICE_NOT_FOUND";

        Assert.IsFalse(output.Open(0));
        StringAssert.Contains(output.LastWriteError, "FT_DEVICE_NOT_FOUND");

        // Same settings again on reconnect: still no tight retry loop.
        Assert.IsFalse(output.Open(0));
        Assert.AreEqual(1, port.Opens.Count);

        port.OpenError = null;
        time.Now += DmxFrameOutput.ReopenInterval;
        output.SendFrameNow();

        Assert.IsTrue(output.IsOpen);
        Assert.AreEqual(1, output.FramesWritten);
    }

    [TestMethod]
    public void Close_SendsOneLastBlackFrame_AndReleasesThePort()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();
        output.Open(0);
        output.SetChannel(1, 255);

        output.Close();

        Assert.AreEqual(1, port.Closes);
        Assert.IsTrue(port.LastFrame.All(b => b == 0));
        Assert.IsFalse(output.IsOpen);
        Assert.IsNull(output.DeviceIndex);

        // Closed means closed: the same index opens again afterwards.
        Assert.IsTrue(output.Open(0));
        Assert.AreEqual(2, port.Opens.Count);
    }

    [TestMethod]
    public async Task Flash_LightsEverything_ThenRestoresAndKeepsValuesThatArrivedMeanwhile()
    {
        (DmxFrameOutput output, FakePort port, _) = Build();
        output.Open(0);
        output.SetChannel(1, 10);
        output.SetChannel(2, 20);

        Task flash = output.FlashAsync(TimeSpan.FromMilliseconds(100));

        output.SendFrameNow();
        Assert.IsTrue(port.LastFrame[1..].All(b => b == 255));

        output.SetChannel(2, 99);
        await flash;
        output.SendFrameNow();

        Assert.AreEqual((byte)10, port.LastFrame[1]);
        Assert.AreEqual((byte)99, port.LastFrame[2]);
        Assert.AreEqual((byte)0, port.LastFrame[3]);
    }

    [TestMethod]
    public void LastNonZeroFrameUtc_TracksFramesWithAnyLightOn()
    {
        (DmxFrameOutput output, _, ManualTime time) = Build();
        output.Open(0);

        output.SendFrameNow();
        Assert.IsNull(output.LastNonZeroFrameUtc);

        output.SetChannel(5, 1);
        output.SendFrameNow();
        Assert.AreEqual(time.Now.UtcDateTime, output.LastNonZeroFrameUtc);
    }

    [TestMethod]
    public async Task FrameThread_KeepsSending_ButNeverFasterThanFortyFramesASecond()
    {
        FakePort port = new();
        using DmxFrameOutput output = new(() => port);

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        output.Open(0);
        await Task.Delay(500);

        int frames;
        lock (port.Frames)
        {
            frames = port.Frames.Count;
        }

        double ms = elapsed.Elapsed.TotalMilliseconds;
        output.Close();

        // A loaded test machine can starve the thread (fewer frames is fine), but the pacing must
        // never let it burst: at most one frame per 25 ms, plus the first one straight away.
        double ceiling = ms / DmxFrameOutput.FrameInterval.TotalMilliseconds + 2;
        Assert.IsTrue(frames >= 3, $"only {frames} frames in {ms:0} ms");
        Assert.IsTrue(frames <= ceiling, $"{frames} frames in {ms:0} ms is faster than 40 Hz");
    }
}
