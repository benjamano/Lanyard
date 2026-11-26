using System.Text;
using Lanyard.Shared.LaserGame.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserCaptureFormatTests
{
    private const string HeaderLine = """{"format":"lanyard-laser-capture","version":1,"recordedAtUtc":"2026-11-26T15:00:00Z"}""";

    // Real Begeara 2 packets (see LaserPacketDecoderTests): game start, a score, game end.
    private static readonly byte[] Start = Convert.FromHexString("342c403031352c30");
    private static readonly byte[] Score = Convert.FromHexString("332c312c302c33302c312c33302c332c31302c30");
    private static readonly byte[] End = Convert.FromHexString("342c403031342c30");

    private static LaserCaptureHeader SampleHeader() => new()
    {
        RecordedAtUtc = new DateTime(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc),
        ClientVersion = "1.0.50",
        Note = "Saturday 3pm",
        Mode = "NextGame",
        Settings = new LaserCaptureSettings
        {
            SourceIp = "192.168.0.10",
            DestinationIp = "192.168.0.255",
            UseCaptureFilter = true,
            PromiscuousCapture = false,
            CaptureFilterApplied = "(udp and (src host 192.168.0.10) and dst host 192.168.0.255)",
            ZoneScoreboardVersion = "Begeara2",
        },
    };

    private static async Task<string> WriteAsync(Func<LaserCaptureWriter, Task> write)
    {
        using MemoryStream stream = new();

        await using (LaserCaptureWriter writer = new(stream, leaveOpen: true))
        {
            await write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task<LaserCapture> ReadAsync(string text)
        => LaserCaptureReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private static async Task<LaserCaptureFormatException> AssertRejectedAsync(string text)
    {
        try
        {
            await ReadAsync(text);
        }
        catch (LaserCaptureFormatException ex)
        {
            return ex;
        }

        Assert.Fail("The file should have been rejected.");
        return null!;
    }

    [TestMethod]
    public async Task RoundTrip_KeepsHeaderPacketsAndFooter()
    {
        string text = await WriteAsync(async writer =>
        {
            await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(0, "192.168.0.10", "192.168.0.255", Start), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(1003, "192.168.0.10", "192.168.0.255", Score), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(900000, null, null, End), CancellationToken.None);
            await writer.WriteFooterAsync(new LaserCaptureFooter { Packets = 3, Dropped = 1, DurationMs = 900000, Games = 1 }, CancellationToken.None);
        });

        LaserCapture capture = await ReadAsync(text);

        Assert.AreEqual(LaserCaptureFormat.FormatName, capture.Header.Format);
        Assert.AreEqual(1, capture.Header.Version);
        Assert.AreEqual(SampleHeader().Settings, capture.Header.Settings);
        Assert.AreEqual("Saturday 3pm", capture.Header.Note);
        Assert.AreEqual(new DateTime(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc), capture.Header.RecordedAtUtc.ToUniversalTime());

        Assert.HasCount(3, capture.Packets);
        CollectionAssert.AreEqual(Score, capture.Packets[1].Payload);
        Assert.AreEqual(1003, capture.Packets[1].OffsetMs);
        Assert.AreEqual("192.168.0.10", capture.Packets[1].SourceIp);
        Assert.IsNull(capture.Packets[2].SourceIp);
        Assert.AreEqual(900000, capture.DurationMs);

        Assert.AreEqual(new LaserCaptureFooter { Packets = 3, Dropped = 1, DurationMs = 900000, Games = 1 }, capture.Footer);
        Assert.IsFalse(capture.Truncated);
    }

    [TestMethod]
    public async Task Writer_WritesOneJsonObjectPerLine_WithHexPayloads()
    {
        string text = await WriteAsync(async writer =>
        {
            await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(12, "192.168.0.10", "192.168.0.255", Start), CancellationToken.None);
        });

        string[] lines = text.Split('\n');

        Assert.HasCount(3, lines, "Header, packet, then the empty string after the final newline.");
        Assert.StartsWith("{\"format\":\"lanyard-laser-capture\",\"version\":1,", lines[0]);
        Assert.AreEqual("""{"t":12,"s":"192.168.0.10","d":"192.168.0.255","p":"342c403031352c30"}""", lines[1]);
        Assert.AreEqual(string.Empty, lines[2]);
    }

    [TestMethod]
    public async Task Writer_RefusesPacketsOutOfOrderOrBeforeTheHeader()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WriteAsync(writer =>
            writer.WritePacketAsync(new LaserCapturePacket(0, null, null, Start), CancellationToken.None)));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => WriteAsync(async writer =>
        {
            await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(50, null, null, Start), CancellationToken.None);
            await writer.WritePacketAsync(new LaserCapturePacket(49, null, null, End), CancellationToken.None);
        }));
    }

    [TestMethod]
    public async Task Writer_CountsBytesWritten()
    {
        using MemoryStream stream = new();
        await using LaserCaptureWriter writer = new(stream, leaveOpen: true);

        await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);
        await writer.WritePacketAsync(new LaserCapturePacket(0, null, null, Start), CancellationToken.None);

        Assert.AreEqual(stream.Length, writer.BytesWritten);
    }

    [TestMethod]
    public async Task Reader_SkipsAPartlyWrittenLastLine()
    {
        string text = HeaderLine + "\n" + """{"t":0,"p":"342c403031352c30"}""" + "\n" + """{"t":5,"p":"332c""";

        LaserCapture capture = await ReadAsync(text);

        Assert.IsTrue(capture.Truncated);
        Assert.HasCount(1, capture.Packets);
        Assert.IsNull(capture.Footer);
    }

    [TestMethod]
    public async Task Reader_AcceptsAMissingFooter()
    {
        LaserCapture capture = await ReadAsync(HeaderLine + "\n" + """{"t":0,"p":"342c403031352c30"}""" + "\n");

        Assert.IsNull(capture.Footer);
        Assert.IsFalse(capture.Truncated);
        Assert.HasCount(1, capture.Packets);
    }

    [TestMethod]
    public async Task Reader_IgnoresFieldsItDoesNotKnow()
    {
        string text = """{"format":"lanyard-laser-capture","version":1,"recordedAtUtc":"2026-11-26T15:00:00Z","futureThing":{"a":1},"settings":{"sourceIp":"1.2.3.4","newSetting":true}}""" + "\n"
            + """{"t":0,"p":"342c403031352c30","rssi":-40}""" + "\n"
            + """{"end":true,"packets":1,"checksum":"abc"}""" + "\n";

        LaserCapture capture = await ReadAsync(text);

        Assert.AreEqual("1.2.3.4", capture.Header.Settings?.SourceIp);
        Assert.HasCount(1, capture.Packets);
        Assert.AreEqual(1, capture.Footer?.Packets);
    }

    [TestMethod]
    public async Task Reader_AcceptsWindowsLineEndings()
    {
        LaserCapture capture = await ReadAsync(HeaderLine + "\r\n" + """{"t":0,"p":"342c403031352c30"}""" + "\r\n");

        Assert.HasCount(1, capture.Packets);
    }

    [TestMethod]
    public async Task Reader_RejectsOtherFiles()
    {
        await AssertRejectedAsync(string.Empty);
        await AssertRejectedAsync("""{"format":"something-else","version":1}""" + "\n");
        await AssertRejectedAsync("hello\n");
        await AssertRejectedAsync("""{"format":"lanyard-laser-capture"}""" + "\n");
        await AssertRejectedAsync("""{"format":"lanyard-laser-capture","version":"1"}""" + "\n");
    }

    [TestMethod]
    public async Task Reader_RejectsANewerFormatVersion()
    {
        LaserCaptureFormatException ex = await AssertRejectedAsync("""{"format":"lanyard-laser-capture","version":2}""" + "\n");

        StringAssert.Contains(ex.Message, "newer");
    }

    [TestMethod]
    public async Task Reader_RejectsBadPackets_NamingTheLine()
    {
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + """{"t":0,"p":"zz"}""" + "\n")).LineNumber);
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + """{"t":0,"p":""}""" + "\n")).LineNumber);
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + """{"p":"34"}""" + "\n")).LineNumber);
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + """{"t":"soon","p":"34"}""" + "\n")).LineNumber);
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + """{"t":-1,"p":"34"}""" + "\n")).LineNumber);
        Assert.AreEqual(2, (await AssertRejectedAsync(HeaderLine + "\n" + "[1,2]" + "\n")).LineNumber);
    }

    [TestMethod]
    public async Task Reader_RejectsABrokenLineThatIsNotTheLast()
    {
        LaserCaptureFormatException ex = await AssertRejectedAsync(HeaderLine + "\n" + """{"t":0,"p":"34""" + "\n" + """{"t":1,"p":"34"}""" + "\n");

        Assert.AreEqual(2, ex.LineNumber);
    }

    [TestMethod]
    public async Task Reader_RejectsTimeGoingBackwards()
    {
        LaserCaptureFormatException ex = await AssertRejectedAsync(HeaderLine + "\n" + """{"t":10,"p":"34"}""" + "\n" + """{"t":9,"p":"34"}""" + "\n");

        Assert.AreEqual(3, ex.LineNumber);
    }

    [TestMethod]
    public async Task Reader_RejectsPacketsAfterTheFooter()
    {
        LaserCaptureFormatException ex = await AssertRejectedAsync(HeaderLine + "\n" + """{"end":true}""" + "\n" + """{"t":1,"p":"34"}""" + "\n");

        Assert.AreEqual(3, ex.LineNumber);
    }

    [TestMethod]
    public async Task Writer_RecoversFromAFailedWrite_SoTheFooterStillGoesOut()
    {
        using MemoryStream inner = new();
        FailingStream stream = new(inner);
        await using LaserCaptureWriter writer = new(stream, leaveOpen: true);

        await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);

        stream.FailNextWrite = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => writer.WritePacketAsync(new LaserCapturePacket(0, null, null, Start), CancellationToken.None));

        await writer.WritePacketAsync(new LaserCapturePacket(5, null, null, End), CancellationToken.None);
        await writer.WriteFooterAsync(new LaserCaptureFooter { Packets = 1 }, CancellationToken.None);

        LaserCapture capture = await ReadAsync(Encoding.UTF8.GetString(inner.ToArray()));

        Assert.HasCount(1, capture.Packets);
        CollectionAssert.AreEqual(End, capture.Packets[0].Payload);
        Assert.IsNotNull(capture.Footer);
    }

    [TestMethod]
    public async Task Writer_FlushesTheHeaderStraightAway()
    {
        using MemoryStream inner = new();
        await using BufferedStream buffered = new(inner, 64 * 1024);
        await using LaserCaptureWriter writer = new(buffered, leaveOpen: true);

        await writer.WriteHeaderAsync(SampleHeader(), CancellationToken.None);

        Assert.IsGreaterThan(0L, inner.Length, "A crash right after starting must still leave a readable header.");
    }

    [TestMethod]
    public async Task Reader_AcceptsAByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(HeaderLine + "\n")];

        LaserCapture capture = await LaserCaptureReader.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.AreEqual(LaserCaptureFormat.FormatName, capture.Header.Format);
    }

    [TestMethod]
    public async Task Reader_RejectsAGameCountTooBigForAnInt()
    {
        await AssertRejectedAsync(HeaderLine + "\n" + """{"end":true,"games":3000000000}""" + "\n");
    }

    [TestMethod]
    public async Task Reader_RejectsAFileTooBigToBeARecording()
    {
        byte[] huge = new byte[LaserCaptureReader.MaxFileBytes + 1];

        await Assert.ThrowsExactlyAsync<LaserCaptureFormatException>(() => LaserCaptureReader.ReadAsync(new MemoryStream(huge), CancellationToken.None));
    }

    private sealed class FailingStream(Stream inner) : Stream
    {
        public bool FailNextWrite { get; set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Disk full");
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
