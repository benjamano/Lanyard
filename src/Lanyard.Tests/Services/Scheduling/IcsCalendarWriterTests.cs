using System.Text;
using Lanyard.Application.Services.Scheduling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class IcsCalendarWriterTests
{
    private static IcsEvent Event(string summary = "Supervisor shift", string? description = "09:00–17:00") => new(
        Uid: "abc@lanyard-shift",
        StartUtc: new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
        EndUtc: new DateTime(2026, 10, 5, 16, 0, 0, DateTimeKind.Utc),
        LastModifiedUtc: new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
        Summary: summary,
        Location: "Ipswich",
        Description: description,
        Url: "https://lanyard.example/rota");

    // RFC 5545 unfolding: a CRLF followed by a single space is removed.
    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty);

    [TestMethod]
    public void Write_ProducesACalendarWithCrlfLineEndingsOnly()
    {
        string ics = IcsCalendarWriter.Write("Shifts – Play2Day", [Event()]);

        StringAssert.StartsWith(ics, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
        StringAssert.EndsWith(ics, "END:VCALENDAR\r\n");
        Assert.IsFalse(ics.Replace("\r\n", string.Empty).Contains('\n'), "Every line break must be CRLF.");
        Assert.IsFalse(ics.Replace("\r\n", string.Empty).Contains('\r'));
    }

    [TestMethod]
    public void Write_WritesTimesInUtcAndTheEventsOwnLastChangeAsDtstamp()
    {
        string ics = Unfold(IcsCalendarWriter.Write("Shifts", [Event()]));

        // 09:00 BST on 5 October is 08:00 UTC.
        StringAssert.Contains(ics, "\r\nDTSTART:20261005T080000Z\r\n");
        StringAssert.Contains(ics, "\r\nDTEND:20261005T160000Z\r\n");
        StringAssert.Contains(ics, "\r\nDTSTAMP:20260930T120000Z\r\n");
        StringAssert.Contains(ics, "\r\nLAST-MODIFIED:20260930T120000Z\r\n");
        StringAssert.Contains(ics, "\r\nUID:abc@lanyard-shift\r\n");
        StringAssert.Contains(ics, "\r\nREFRESH-INTERVAL;VALUE=DURATION:PT1H\r\n");
        StringAssert.Contains(ics, "\r\nX-PUBLISHED-TTL:PT1H\r\n");
    }

    [TestMethod]
    public void Write_IsByteIdenticalForTheSameInput()
    {
        Assert.AreEqual(IcsCalendarWriter.Write("Shifts", [Event()]), IcsCalendarWriter.Write("Shifts", [Event()]));
    }

    [TestMethod]
    public void Write_EmptyCalendarHasNoEvents()
    {
        string ics = IcsCalendarWriter.Write("Shifts", []);

        Assert.IsFalse(ics.Contains("BEGIN:VEVENT"));
        StringAssert.Contains(ics, "X-WR-CALNAME:Shifts\r\n");
    }

    [TestMethod]
    [DataRow("a,b", "a\\,b")]
    [DataRow("a;b", "a\\;b")]
    [DataRow("a\\b", "a\\\\b")]
    [DataRow("line1\nline2", "line1\\nline2")]
    [DataRow("line1\r\nline2", "line1\\nline2")]
    [DataRow("tab\tstripped", "tabstripped")]
    public void Escape_EscapesTextSpecials(string input, string expected)
    {
        Assert.AreEqual(expected, IcsCalendarWriter.Escape(input));
    }

    [TestMethod]
    public void Write_EscapesEventText()
    {
        string ics = Unfold(IcsCalendarWriter.Write("Shifts", [Event(summary: "Bar, kitchen; floor")]));

        StringAssert.Contains(ics, "\r\nSUMMARY:Bar\\, kitchen\\; floor\r\n");
    }

    [TestMethod]
    public void Fold_LeavesShortLinesAlone()
    {
        string line = new('a', 75);

        Assert.AreEqual(line, IcsCalendarWriter.Fold(line));
    }

    [TestMethod]
    public void Fold_SplitsLongLinesAt75Octets()
    {
        string line = "DESCRIPTION:" + new string('x', 200);

        string folded = IcsCalendarWriter.Fold(line);
        string[] physical = folded.Split("\r\n");

        Assert.IsTrue(physical.Length > 1);
        Assert.IsTrue(physical.All(x => Encoding.UTF8.GetByteCount(x) <= 75));
        Assert.IsTrue(physical.Skip(1).All(x => x.StartsWith(' ')));
        Assert.AreEqual(line, Unfold(folded));
    }

    [TestMethod]
    public void Fold_NeverSplitsAMultiByteCharacter()
    {
        // é is 2 octets and 🎉 is 4 (a surrogate pair), so a naive 75-char or 75-byte cut would
        // land inside one of them somewhere along the line.
        string line = "SUMMARY:" + string.Concat(Enumerable.Repeat("é🎉a", 40));

        string folded = IcsCalendarWriter.Fold(line);

        foreach (string physical in folded.Split("\r\n"))
        {
            Assert.IsTrue(Encoding.UTF8.GetByteCount(physical) <= 75);
            Assert.IsFalse(physical.Length > 0 && char.IsHighSurrogate(physical[^1]), "A surrogate pair was split.");
            Assert.IsFalse(physical.TrimStart(' ').Length > 0 && char.IsLowSurrogate(physical.TrimStart(' ')[0]), "A surrogate pair was split.");
        }

        Assert.AreEqual(line, Unfold(folded));
    }
}
