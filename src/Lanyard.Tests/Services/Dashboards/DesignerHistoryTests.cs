using Lanyard.App.Components.Dashboards.Designer;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dashboards;

[TestClass]
public class DesignerHistoryTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static Dashboard NewDashboard() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Front desk",
        Widgets = [new DigitalClockWidget { GridX = 0, GridY = 0 }]
    };

    [TestMethod]
    public void UndoAndRedo_RestoreLayoutAndSettings()
    {
        ManualTime time = new();
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new(time);
        history.Reset(dashboard);

        dashboard.Widgets[0].GridX = 4;
        history.Record(dashboard, "Moved Clock");
        time.Advance(TimeSpan.FromSeconds(5));
        ((DigitalClockWidget)dashboard.Widgets[0]).ShowDate = false;
        history.Record(dashboard, "Hid the date");

        Assert.IsTrue(history.Undo(dashboard));
        Assert.IsTrue(((DigitalClockWidget)dashboard.Widgets[0]).ShowDate);
        Assert.AreEqual(4, dashboard.Widgets[0].GridX);

        Assert.IsTrue(history.Undo(dashboard));
        Assert.AreEqual(0, dashboard.Widgets[0].GridX);
        Assert.IsFalse(history.CanUndo);

        Assert.IsTrue(history.Redo(dashboard));
        Assert.AreEqual(4, dashboard.Widgets[0].GridX);
        Assert.AreEqual("Hid the date", history.RedoLabel);
    }

    [TestMethod]
    public void Undo_BringsBackARemovedWidgetWithTheSameId()
    {
        Dashboard dashboard = NewDashboard();
        Guid id = dashboard.Widgets[0].Id;
        DesignerHistory history = new();
        history.Reset(dashboard);

        dashboard.Widgets.Clear();
        history.Record(dashboard, "Removed Clock");
        history.Undo(dashboard);

        Assert.AreEqual(id, dashboard.Widgets.Single().Id);
        Assert.IsInstanceOfType<DigitalClockWidget>(dashboard.Widgets.Single());
    }

    [TestMethod]
    public void RepeatedEditsOfTheSameKind_AreUndoneTogether()
    {
        ManualTime time = new();
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new(time);
        history.Reset(dashboard);

        foreach (string title in new[] { "C", "Cl", "Clo", "Cloc", "Clock" })
        {
            dashboard.Widgets[0].Title = title;
            history.Record(dashboard, "Changed title");
            time.Advance(TimeSpan.FromMilliseconds(300));
        }

        history.Undo(dashboard);

        Assert.IsNull(dashboard.Widgets[0].Title);
        Assert.IsFalse(history.CanUndo);
    }

    [TestMethod]
    public void UnsavedChanges_ClearWhenSavedOrUndoneBackToTheSavedState()
    {
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new();
        history.Reset(dashboard);
        Assert.IsFalse(history.HasUnsavedChanges);

        dashboard.Widgets[0].GridY = 2;
        history.Record(dashboard, "Moved Clock");
        Assert.IsTrue(history.HasUnsavedChanges);

        history.Undo(dashboard);
        Assert.IsFalse(history.HasUnsavedChanges);

        history.Redo(dashboard);
        history.MarkSaved(dashboard);
        Assert.IsFalse(history.HasUnsavedChanges);
    }

    [TestMethod]
    public void Restore_GivesCopiesSoLaterEditsDontChangeHistory()
    {
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new();
        history.Reset(dashboard);

        dashboard.Widgets[0].GridX = 6;
        history.Record(dashboard, "Moved Clock");
        history.Undo(dashboard);

        dashboard.Widgets[0].GridX = 9; // edits the restored copy, not the history entry
        history.Redo(dashboard);

        Assert.AreEqual(6, dashboard.Widgets[0].GridX);
    }

    [TestMethod]
    public void History_KeepsAtMostFiftyEntries()
    {
        ManualTime time = new();
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new(time);
        history.Reset(dashboard);

        for (int i = 1; i <= 80; i++)
        {
            dashboard.Widgets[0].GridY = i;
            history.Record(dashboard, $"Move {i}");
            time.Advance(TimeSpan.FromSeconds(2));
        }

        int undos = 0;

        while (history.Undo(dashboard))
        {
            undos++;
        }

        Assert.AreEqual(DesignerHistory.MaxEntries - 1, undos);
        Assert.AreEqual(31, dashboard.Widgets[0].GridY);
    }

    [TestMethod]
    public void QuickMoves_AreEachTheirOwnStep()
    {
        ManualTime time = new();
        Dashboard dashboard = NewDashboard();
        DesignerHistory history = new(time);
        history.Reset(dashboard);

        dashboard.Widgets[0].GridX = 1;
        history.Record(dashboard, "Moved Clock");
        time.Advance(TimeSpan.FromMilliseconds(200));
        dashboard.Widgets[0].GridX = 2;
        history.Record(dashboard, "Moved Clock");

        history.Undo(dashboard);

        Assert.AreEqual(1, dashboard.Widgets[0].GridX);
    }
}
