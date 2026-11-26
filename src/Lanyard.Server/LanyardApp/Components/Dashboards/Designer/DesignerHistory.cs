using System.Globalization;
using System.Reflection;
using System.Text;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Dashboards.Designer;

// A copy of everything the designer can change about a dashboard: its own details and every
// widget with all of its settings and layout.
public sealed class DashboardSnapshot
{
    private DashboardSnapshot(string name, string? description, int rowHeight, bool fitToScreen, List<DashboardWidget> widgets, string label)
    {
        Name = name;
        Description = description;
        RowHeight = rowHeight;
        FitToScreen = fitToScreen;
        Widgets = widgets;
        Label = label;
        Fingerprint = BuildFingerprint(name, description, rowHeight, fitToScreen, widgets);
    }

    public string Name { get; }
    public string? Description { get; }
    public int RowHeight { get; }
    public bool FitToScreen { get; }
    public List<DashboardWidget> Widgets { get; }

    // What the change that produced this state was ("Moved Clock"), for the undo/redo tooltips.
    public string Label { get; }

    // Equal fingerprints mean equal content, which is how the designer knows whether there's
    // anything unsaved (undoing back to the saved state counts as saved again).
    public string Fingerprint { get; }

    public static DashboardSnapshot Capture(Dashboard dashboard, string label) =>
        new(dashboard.Name, dashboard.Description, dashboard.RowHeight, dashboard.FitToScreen,
            [.. dashboard.Widgets.Select(DashboardWidgetCloner.CloneExact)], label);

    // Puts the dashboard back to this state. The widget list is refilled in place (the canvas
    // holds a reference to it) with fresh copies, so later edits can't reach into this snapshot.
    public void Restore(Dashboard dashboard)
    {
        dashboard.Name = Name;
        dashboard.Description = Description;
        dashboard.RowHeight = RowHeight;
        dashboard.FitToScreen = FitToScreen;
        dashboard.Widgets.Clear();
        dashboard.Widgets.AddRange(Widgets.Select(DashboardWidgetCloner.CloneExact));
    }

    private static string BuildFingerprint(string name, string? description, int rowHeight, bool fitToScreen, List<DashboardWidget> widgets)
    {
        StringBuilder builder = new();
        builder.Append(name).Append('\u001f').Append(description).Append('\u001f').Append(rowHeight).Append('\u001f').Append(fitToScreen);

        foreach (DashboardWidget widget in widgets.OrderBy(w => w.Id))
        {
            builder.Append('\u001e').Append(widget.GetType().Name);

            foreach (PropertyInfo property in DashboardWidgetCloner.CopiedProperties(widget.GetType()))
            {
                builder.Append('\u001f').Append(property.Name).Append('=')
                    .Append(Convert.ToString(property.GetValue(widget), CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}

// Undo and redo for the designer: a list of states, newest last, and where we are in it.
public sealed class DesignerHistory
{
    public const int MaxEntries = 50;

    // Typing a title or a text widget's words raises a change per keystroke. Edits with the
    // same description this close together are folded into one, so one undo removes the whole
    // edit. Moves, resizes and adds are never folded: each arrow-key press is its own step.
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private static readonly string[] NeverMerged = ["Moved", "Resized", "Added", "Removed", "Duplicated", "Tidied", "Saved"];

    private readonly List<DashboardSnapshot> _entries = [];
    private readonly TimeProvider _time;
    private DateTimeOffset _lastRecorded;
    private int _index = -1;
    private string _savedFingerprint = string.Empty;

    public DesignerHistory(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _entries.Count - 1;

    public string? UndoLabel => CanUndo ? _entries[_index].Label : null;
    public string? RedoLabel => CanRedo ? _entries[_index + 1].Label : null;

    public bool HasUnsavedChanges => _index >= 0 && _entries[_index].Fingerprint != _savedFingerprint;

    // Starts over from a freshly loaded (or reloaded) dashboard, which counts as saved.
    public void Reset(Dashboard dashboard)
    {
        _entries.Clear();
        _entries.Add(DashboardSnapshot.Capture(dashboard, "Opened"));
        _index = 0;
        _savedFingerprint = _entries[0].Fingerprint;
        _lastRecorded = DateTimeOffset.MinValue;
    }

    // Records the dashboard as it is now, after a change described by `label`.
    public void Record(Dashboard dashboard, string label)
    {
        DashboardSnapshot snapshot = DashboardSnapshot.Capture(dashboard, label);
        DateTimeOffset now = _time.GetUtcNow();

        if (_index >= 0 && snapshot.Fingerprint == _entries[_index].Fingerprint)
        {
            return;
        }

        // A new change after undoing throws away what could have been redone.
        if (CanRedo)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        bool merge = _index > 0
            && _entries[_index].Label == label
            && now - _lastRecorded < MergeWindow
            && !NeverMerged.Any(prefix => label.StartsWith(prefix, StringComparison.Ordinal));

        if (merge)
        {
            _entries[_index] = snapshot;
        }
        else
        {
            _entries.Add(snapshot);
            _index++;

            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
                _index--;
            }
        }

        _lastRecorded = now;
    }

    public bool Undo(Dashboard dashboard)
    {
        if (!CanUndo)
        {
            return false;
        }

        _index--;
        _entries[_index].Restore(dashboard);
        _lastRecorded = DateTimeOffset.MinValue;
        return true;
    }

    public bool Redo(Dashboard dashboard)
    {
        if (!CanRedo)
        {
            return false;
        }

        _index++;
        _entries[_index].Restore(dashboard);
        _lastRecorded = DateTimeOffset.MinValue;
        return true;
    }

    // The dashboard as it is now has been saved.
    public void MarkSaved(Dashboard dashboard)
    {
        Record(dashboard, "Saved");
        _savedFingerprint = _entries[_index].Fingerprint;
    }
}
