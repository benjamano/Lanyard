using Lanyard.App.Components.WhatsOn;
using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.WhatsOn;

[TestClass]
public class TimelineLayoutTests
{
    // Before the clocks change, so venue-local time equals UTC.
    private static readonly DateOnly Day = new(2026, 3, 14);

    private static WhatsOnItem Item(string title, int startHour, int startMinute, int minutes) => new()
    {
        Kind = WhatsOnKind.Party,
        Id = Guid.NewGuid(),
        Title = title,
        StartUtc = RotaTime.ToUtc(Day, new TimeOnly(startHour, startMinute)),
        EndUtc = RotaTime.ToUtc(Day, new TimeOnly(startHour, startMinute)).AddMinutes(minutes)
    };

    private static TimelineLayout.Block BlockFor(List<TimelineLayout.Block> blocks, string title) => blocks.Single(x => x.Item.Title == title);

    [TestMethod]
    public void Layout_OverlappingEntriesShareTheirClusterSideBySide()
    {
        List<TimelineLayout.Block> blocks = TimelineLayout.Layout(
        [
            Item("A", 10, 0, 120),
            Item("B", 10, 30, 120),
            Item("C", 12, 0, 60),   // A has finished: reuses lane 0, still in the A/B cluster.
            Item("D", 15, 0, 120)   // Nothing else on: a cluster of its own.
        ], Day);

        Assert.AreEqual((0, 2), (BlockFor(blocks, "A").Lane, BlockFor(blocks, "A").Lanes));
        Assert.AreEqual((1, 2), (BlockFor(blocks, "B").Lane, BlockFor(blocks, "B").Lanes));
        Assert.AreEqual((0, 2), (BlockFor(blocks, "C").Lane, BlockFor(blocks, "C").Lanes));
        Assert.AreEqual((0, 1), (BlockFor(blocks, "D").Lane, BlockFor(blocks, "D").Lanes));
        Assert.AreEqual(10 * 60, BlockFor(blocks, "A").StartMinute);
        Assert.AreEqual(12 * 60, BlockFor(blocks, "A").EndMinute);
    }

    [TestMethod]
    public void Layout_BackToBackEntriesDontOverlap()
    {
        List<TimelineLayout.Block> blocks = TimelineLayout.Layout([Item("A", 10, 0, 120), Item("B", 12, 0, 120)], Day);

        Assert.IsTrue(blocks.All(x => x.Lanes == 1 && x.Lane == 0));
    }

    [TestMethod]
    public void Layout_AShortEntryStillGetsRoomForItsTitle()
    {
        TimelineLayout.Block block = TimelineLayout.Layout([Item("A", 10, 0, 10)], Day).Single();

        Assert.AreEqual(TimelineLayout.MinimumMinutes, block.EndMinute - block.StartMinute);
    }

    [TestMethod]
    public void Minutes_AnEntryRunningPastMidnightStopsAtMidnight()
    {
        (int start, int end) = TimelineLayout.Minutes(Item("Late", 23, 0, 120), Day);

        Assert.AreEqual(23 * 60, start);
        Assert.AreEqual(24 * 60, end);
    }

    [TestMethod]
    public void WindowFor_UsesTheDefaultHours_StretchedToWholeHoursToFitEverything()
    {
        Assert.AreEqual(new TimelineLayout.Window(TimelineLayout.DefaultStartHour, TimelineLayout.DefaultEndHour), TimelineLayout.WindowFor([], Day));
        Assert.AreEqual(new TimelineLayout.Window(9, 18), TimelineLayout.WindowFor([Item("A", 10, 0, 120)], Day));
        Assert.AreEqual(new TimelineLayout.Window(8, 20), TimelineLayout.WindowFor([Item("Early", 8, 30, 60), Item("Late", 17, 15, 120)], Day));
        Assert.AreEqual(new TimelineLayout.Window(9, 24), TimelineLayout.WindowFor([Item("Late", 23, 0, 120)], Day));
    }
}
