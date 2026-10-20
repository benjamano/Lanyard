using Lanyard.Shared.Diagnostics;
using Lanyard.Shared.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Diagnostics;

[TestClass]
public class LogLineBufferTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static void AddLines(LogLineBuffer buffer, int count, int startAt = 0)
    {
        for (int i = startAt; i < startAt + count; i++)
        {
            buffer.Add(Start.AddSeconds(i), "Information", "Lanyard.Client.Test", $"line {i}");
        }
    }

    [TestMethod]
    public void Snapshot_ReturnsLinesOldestFirst()
    {
        LogLineBuffer buffer = new(10);
        AddLines(buffer, 3);

        ClientLogTailDTO tail = buffer.Snapshot(10, int.MaxValue);

        CollectionAssert.AreEqual(new[] { "line 0", "line 1", "line 2" }, tail.Lines.Select(x => x.Message).ToArray());
        Assert.IsFalse(tail.Truncated);
    }

    [TestMethod]
    public void Add_OverwritesTheOldestLinesOnceFull()
    {
        LogLineBuffer buffer = new(5);
        AddLines(buffer, 12);

        ClientLogTailDTO tail = buffer.Snapshot(100, int.MaxValue);

        CollectionAssert.AreEqual(new[] { "line 7", "line 8", "line 9", "line 10", "line 11" }, tail.Lines.Select(x => x.Message).ToArray());
    }

    [TestMethod]
    public void Snapshot_TakesTheNewestLinesWhenAskedForFewer()
    {
        LogLineBuffer buffer = new(10);
        AddLines(buffer, 6);

        ClientLogTailDTO tail = buffer.Snapshot(2, int.MaxValue);

        CollectionAssert.AreEqual(new[] { "line 4", "line 5" }, tail.Lines.Select(x => x.Message).ToArray());
        Assert.IsFalse(tail.Truncated, "Asking for fewer lines is not truncation.");
    }

    [TestMethod]
    public void Snapshot_DropsTheOldestLinesToFitTheByteCap()
    {
        LogLineBuffer buffer = new(500);

        for (int i = 0; i < 500; i++)
        {
            buffer.Add(Start.AddSeconds(i), "Information", "Lanyard.Client.Test", $"line {i} " + new string('x', 900));
        }

        const int maxBytes = 50 * 1024;
        ClientLogTailDTO tail = buffer.Snapshot(500, maxBytes);

        Assert.IsTrue(tail.Truncated);
        Assert.IsTrue(tail.Lines.Count is > 0 and < 500);
        StringAssert.StartsWith(tail.Lines[^1].Message, "line 499 ", "The newest line must always be kept.");
        Assert.IsLessThanOrEqualTo(maxBytes, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tail).Length);
    }

    [TestMethod]
    public void Add_CapsLongMessagesAndExceptions()
    {
        LogLineBuffer buffer = new(5);
        buffer.Add(Start, "Error", "Lanyard.Client.Test", new string('m', 5000), new string('e', 9000));

        string message = buffer.Snapshot(5, int.MaxValue).Lines.Single().Message;
        string[] parts = message.Split(Environment.NewLine);

        Assert.AreEqual(LogLineBuffer.MaxMessageLength + 1, parts[0].Length, "Message is capped, plus the ellipsis.");
        Assert.AreEqual(LogLineBuffer.MaxExceptionLength + 1, parts[1].Length, "Exception is capped, plus the ellipsis.");
    }

    [TestMethod]
    public async Task Add_IsSafeFromManyThreads()
    {
        LogLineBuffer buffer = new(500);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() => AddLines(buffer, 1000, thread * 1000))));

        ClientLogTailDTO tail = buffer.Snapshot(500, int.MaxValue);

        Assert.HasCount(500, tail.Lines);
        Assert.IsTrue(tail.Lines.All(x => x != null));
    }
}
