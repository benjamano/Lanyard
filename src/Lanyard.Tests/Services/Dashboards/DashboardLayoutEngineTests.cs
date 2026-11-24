using Lanyard.Application.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dashboards;

[TestClass]
public class DashboardLayoutEngineTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private static WidgetRect Get(IEnumerable<WidgetRect> layout, Guid id) => layout.Single(r => r.Id == id);

    private static void AssertNoOverlaps(IReadOnlyList<WidgetRect> layout)
    {
        for (int i = 0; i < layout.Count; i++)
        {
            for (int j = i + 1; j < layout.Count; j++)
            {
                Assert.IsFalse(layout[i].Overlaps(layout[j]), $"{layout[i]} overlaps {layout[j]}");
            }
        }

        foreach (WidgetRect rect in layout)
        {
            Assert.IsTrue(rect.X >= 0 && rect.Right <= DashboardLayoutEngine.Columns && rect.Y >= 0, $"{rect} is outside the grid");
        }
    }

    [TestMethod]
    public void Overlaps_TouchingEdgesDoNotOverlap()
    {
        WidgetRect left = new(A, 0, 0, 4, 2);

        Assert.IsFalse(left.Overlaps(new WidgetRect(B, 4, 0, 4, 2)));
        Assert.IsFalse(left.Overlaps(new WidgetRect(B, 0, 2, 4, 2)));
        Assert.IsTrue(left.Overlaps(new WidgetRect(B, 3, 1, 4, 2)));
    }

    [TestMethod]
    public void Clamp_KeepsWidgetsInsideTheTwelveColumns()
    {
        Assert.AreEqual(new WidgetRect(A, 8, 0, 4, 1), DashboardLayoutEngine.Clamp(new WidgetRect(A, 10, -3, 4, 0)));
        Assert.AreEqual(new WidgetRect(A, 0, 2, 12, 3), DashboardLayoutEngine.Clamp(new WidgetRect(A, 5, 2, 40, 3)));
        Assert.AreEqual(new WidgetRect(A, 0, 0, 1, 1), DashboardLayoutEngine.Clamp(new WidgetRect(A, -1, 0, 0, -2)));
    }

    [TestMethod]
    public void Normalize_LeavesAValidLayoutAloneIncludingItsGaps()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 6, 5, 4, 2)];

        CollectionAssert.AreEqual(layout, DashboardLayoutEngine.Normalize(layout));
    }

    [TestMethod]
    public void Normalize_MovesALaterOverlappingWidgetToTheNextFreeSpot()
    {
        List<WidgetRect> result = DashboardLayoutEngine.Normalize([new(A, 0, 0, 4, 2), new(B, 2, 1, 4, 2)]);

        Assert.AreEqual(new WidgetRect(A, 0, 0, 4, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 4, 0, 4, 2), Get(result, B));
    }

    [TestMethod]
    public void Normalize_PullsAnOverflowingWidgetBackInsideTheGrid()
    {
        List<WidgetRect> result = DashboardLayoutEngine.Normalize([new(A, 11, 0, 4, 1)]);

        Assert.AreEqual(new WidgetRect(A, 8, 0, 4, 1), Get(result, A));
    }

    [TestMethod]
    public void Move_IntoFreeSpaceMovesOnlyThatWidget()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 4, 0, 4, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Move(layout, A, 8, 3);

        Assert.AreEqual(new WidgetRect(A, 8, 3, 4, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 4, 0, 4, 2), Get(result, B));
    }

    [TestMethod]
    public void Move_OntoAnotherWidgetPushesItDown()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 4, 0, 4, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Move(layout, A, 4, 0);

        Assert.AreEqual(new WidgetRect(A, 4, 0, 4, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 4, 2, 4, 2), Get(result, B));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Move_PushesInAChainWhenThePushedWidgetLandsOnAnother()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 6, 2), new(B, 6, 0, 6, 2), new(C, 6, 2, 6, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Move(layout, A, 6, 0);

        Assert.AreEqual(new WidgetRect(A, 6, 0, 6, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 6, 2, 6, 2), Get(result, B));
        Assert.AreEqual(new WidgetRect(C, 6, 4, 6, 2), Get(result, C));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Move_PastTheRightEdgeIsClamped()
    {
        List<WidgetRect> result = DashboardLayoutEngine.Move([new(A, 0, 0, 4, 1)], A, 11, 0);

        Assert.AreEqual(new WidgetRect(A, 8, 0, 4, 1), Get(result, A));
    }

    [TestMethod]
    public void Move_UnknownIdChangesNothing()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 1)];

        CollectionAssert.AreEqual(layout, DashboardLayoutEngine.Move(layout, B, 3, 3));
    }

    [TestMethod]
    public void Move_DoesNotMutateTheInput()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 4, 0, 4, 2)];
        List<WidgetRect> copy = [.. layout];

        DashboardLayoutEngine.Move(layout, A, 4, 0);

        CollectionAssert.AreEqual(copy, layout);
    }

    [TestMethod]
    public void Move_KeepsTheListInItsOriginalOrder()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 4, 0, 4, 2), new(C, 8, 0, 4, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Move(layout, C, 0, 0);

        CollectionAssert.AreEqual(new[] { A, B, C }, result.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public void Move_WithCompactClosesTheGapLeftBehind()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 12, 2), new(B, 0, 2, 12, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Move(layout, A, 0, 6, compact: true);

        // A stays where it was dropped; B floats up into the space A left.
        Assert.AreEqual(new WidgetRect(B, 0, 0, 12, 2), Get(result, B));
        Assert.AreEqual(new WidgetRect(A, 0, 6, 12, 2), Get(result, A));
    }

    [TestMethod]
    public void Resize_GrowingIntoANeighbourPushesItDown()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 4, 0, 4, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Resize(layout, A, 6, 2);

        Assert.AreEqual(new WidgetRect(A, 0, 0, 6, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 4, 2, 4, 2), Get(result, B));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Resize_TallerPushesWidgetsBelow()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 4, 2), new(B, 0, 2, 4, 2), new(C, 0, 4, 4, 1)];

        List<WidgetRect> result = DashboardLayoutEngine.Resize(layout, A, 4, 3);

        Assert.AreEqual(new WidgetRect(B, 0, 3, 4, 2), Get(result, B));
        Assert.AreEqual(new WidgetRect(C, 0, 5, 4, 1), Get(result, C));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Resize_CannotGrowPastTheRightEdgeOrBelowOneCell()
    {
        List<WidgetRect> layout = [new(A, 8, 0, 2, 2)];

        Assert.AreEqual(new WidgetRect(A, 8, 0, 4, 2), Get(DashboardLayoutEngine.Resize(layout, A, 9, 2), A));
        Assert.AreEqual(new WidgetRect(A, 8, 0, 1, 1), Get(DashboardLayoutEngine.Resize(layout, A, 0, 0), A));
    }

    [TestMethod]
    public void Place_WithoutAPositionTakesTheFirstFreeSpot()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 8, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Place(layout, new WidgetRect(B, 0, 0, 4, 1), atPosition: false);

        Assert.AreEqual(new WidgetRect(B, 8, 0, 4, 1), Get(result, B));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Place_WithoutAPositionGoesBelowWhenTheTopRowIsFull()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 6, 2), new(B, 6, 0, 6, 1)];

        List<WidgetRect> result = DashboardLayoutEngine.Place(layout, new WidgetRect(C, 0, 0, 6, 2), atPosition: false);

        Assert.AreEqual(new WidgetRect(C, 6, 1, 6, 2), Get(result, C));
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Place_AtAPositionLandsThereAndPushesOthersDown()
    {
        List<WidgetRect> layout = [new(A, 0, 0, 12, 2)];

        List<WidgetRect> result = DashboardLayoutEngine.Place(layout, new WidgetRect(B, 2, 0, 4, 1), atPosition: true);

        Assert.AreEqual(new WidgetRect(B, 2, 0, 4, 1), Get(result, B));
        Assert.AreEqual(new WidgetRect(A, 0, 1, 12, 2), Get(result, A));
        CollectionAssert.AreEqual(new[] { A, B }, result.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public void Compact_FloatsEverythingUpWithoutReordering()
    {
        List<WidgetRect> layout = [new(A, 0, 3, 6, 2), new(B, 6, 6, 6, 1), new(C, 0, 8, 6, 1)];

        List<WidgetRect> result = DashboardLayoutEngine.Compact(layout);

        Assert.AreEqual(new WidgetRect(A, 0, 0, 6, 2), Get(result, A));
        Assert.AreEqual(new WidgetRect(B, 6, 0, 6, 1), Get(result, B));
        Assert.AreEqual(new WidgetRect(C, 0, 2, 6, 1), Get(result, C));
        CollectionAssert.AreEqual(new[] { A, B, C }, result.Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public void RowCount_IsTheLowestBottomEdge()
    {
        Assert.AreEqual(0, DashboardLayoutEngine.RowCount([]));
        Assert.AreEqual(7, DashboardLayoutEngine.RowCount([new(A, 0, 0, 2, 2), new(B, 0, 4, 2, 3)]));
    }

    // Throws a few hundred random moves and resizes at a busy layout: whatever happens, nothing
    // may end up overlapping or outside the grid, and the moved widget must land where it was put.
    [TestMethod]
    public void RandomMovesAndResizes_NeverLeaveOverlaps()
    {
        Random random = new(1234);
        Guid[] ids = [A, B, C, D, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        List<WidgetRect> layout = DashboardLayoutEngine.Normalize(ids.Select(id => new WidgetRect(id, 0, 0, random.Next(1, 7), random.Next(1, 4))));

        for (int step = 0; step < 400; step++)
        {
            Guid id = ids[random.Next(ids.Length)];
            bool compact = random.Next(2) == 0;

            if (random.Next(2) == 0)
            {
                int x = random.Next(0, 12);
                int y = random.Next(0, 10);
                layout = DashboardLayoutEngine.Move(layout, id, x, y, compact);

                WidgetRect moved = Get(layout, id);
                Assert.AreEqual(Math.Min(x, 12 - moved.W), moved.X);

                if (!compact)
                {
                    Assert.AreEqual(y, moved.Y);
                }
            }
            else
            {
                layout = DashboardLayoutEngine.Resize(layout, id, random.Next(1, 13), random.Next(1, 5), compact);
            }

            AssertNoOverlaps(layout);
            Assert.HasCount(ids.Length, layout);
        }
    }
}
