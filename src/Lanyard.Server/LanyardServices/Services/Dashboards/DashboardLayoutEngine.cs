namespace Lanyard.Application.Services;

// One widget's place on the dashboard grid, in cells. X and Y are zero-based; W and H are spans.
public readonly record struct WidgetRect(Guid Id, int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;

    public bool Overlaps(WidgetRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

// The rules for laying widgets out on a dashboard's grid, kept free of the EF models and of Blazor
// so they can be unit tested directly. The designer calls Move/Resize while a widget is being
// dragged to preview where everything will end up, and again on drop to commit it.
//
// The model is gridstack's "float" mode: the widget being moved or resized wins its spot and
// anything it now overlaps is pushed straight down out of its way (which can push further widgets
// in turn). Nothing else moves, so gaps someone left on purpose - common on a TV layout - stay.
// Compact (the designer's "Tidy up") floats everything up to close the gaps when that's wanted,
// and every operation takes compact: true to do the same straight after. Every operation returns
// a new list and never mutates the input.
public static class DashboardLayoutEngine
{
    public const int Columns = 12;

    // A safety stop for the push-down loop. Every pass moves at least one widget further down a
    // bounded grid, so it always ends well before this; it only guards against a bug looping forever.
    private const int MaxSettlePasses = 10_000;

    // Clamps every rect into the grid and resolves overlaps, in the given order. Earlier widgets
    // keep their place and later ones that collide (or no longer fit) move to the first free spot,
    // scanning row by row. This is the same behaviour as the old grid's
    // SetInvalidGridPositionsToTheNextFreePosition, so layouts saved before the designer come out
    // the same. Deliberately no compaction: a layout someone built with gaps keeps them.
    public static List<WidgetRect> Normalize(IEnumerable<WidgetRect> layout)
    {
        List<WidgetRect> placed = [];

        foreach (WidgetRect raw in layout)
        {
            WidgetRect rect = Clamp(raw);

            if (placed.Any(rect.Overlaps))
            {
                (int x, int y) = FindFreeSpot(placed, rect.W, rect.H);
                rect = rect with { X = x, Y = y };
            }

            placed.Add(rect);
        }

        return placed;
    }

    // Keeps a rect inside the 12 columns: width 1-12, height at least 1, and X pulled back so the
    // right edge never passes the last column.
    public static WidgetRect Clamp(WidgetRect rect)
    {
        int w = Math.Clamp(rect.W, 1, Columns);
        int h = Math.Max(1, rect.H);
        int x = Math.Clamp(rect.X, 0, Columns - w);
        int y = Math.Max(0, rect.Y);

        return rect with { X = x, Y = y, W = w, H = h };
    }

    // Moves one widget so its top-left corner is at (x, y), pushing anything in the way down.
    public static List<WidgetRect> Move(IReadOnlyList<WidgetRect> layout, Guid id, int x, int y, bool compact = false)
    {
        int index = IndexOf(layout, id);

        if (index < 0)
        {
            return [.. layout];
        }

        WidgetRect moved = Clamp(layout[index] with { X = x, Y = y });

        return Settle(layout, moved, compact);
    }

    // Resizes one widget (its top-left corner stays put), pushing anything in the way down.
    public static List<WidgetRect> Resize(IReadOnlyList<WidgetRect> layout, Guid id, int w, int h, bool compact = false)
    {
        int index = IndexOf(layout, id);

        if (index < 0)
        {
            return [.. layout];
        }

        WidgetRect current = layout[index];
        int clampedW = Math.Clamp(w, 1, Columns - current.X);
        WidgetRect resized = Clamp(current with { W = clampedW, H = h });

        return Settle(layout, resized, compact);
    }

    // Places a new widget. With a position it goes exactly there and pushes others down (a drop
    // from the palette); without one it goes in the first free spot, scanning row by row (a tap
    // on a palette item).
    public static List<WidgetRect> Place(IReadOnlyList<WidgetRect> layout, WidgetRect newWidget, bool atPosition, bool compact = false)
    {
        List<WidgetRect> without = [.. layout.Where(r => r.Id != newWidget.Id)];
        WidgetRect rect = Clamp(newWidget);

        if (!atPosition)
        {
            (int x, int y) = FindFreeSpot(without, rect.W, rect.H);
            without.Add(rect with { X = x, Y = y });

            return compact ? Compact(without) : without;
        }

        without.Add(rect);

        return Settle(without, rect, compact);
    }

    // Floats every widget up as far as it can go without overlapping anything above it, top-most
    // widgets first, so a layout never has a hole left where a widget used to be.
    public static List<WidgetRect> Compact(IReadOnlyList<WidgetRect> layout)
    {
        List<WidgetRect> ordered = [.. layout.OrderBy(r => r.Y).ThenBy(r => r.X)];
        List<WidgetRect> settled = [];

        foreach (WidgetRect rect in ordered)
        {
            WidgetRect current = rect;

            while (current.Y > 0)
            {
                WidgetRect up = current with { Y = current.Y - 1 };

                if (settled.Any(up.Overlaps))
                {
                    break;
                }

                current = up;
            }

            settled.Add(current);
        }

        // Callers rely on the list keeping its original order (it maps back to the widget list).
        Dictionary<Guid, WidgetRect> byId = settled.ToDictionary(r => r.Id);

        return [.. layout.Select(r => byId[r.Id])];
    }

    // The lowest occupied row plus one, i.e. how many rows the layout needs.
    public static int RowCount(IEnumerable<WidgetRect> layout) =>
        layout.Select(r => r.Bottom).DefaultIfEmpty(0).Max();

    // The first spot, scanning left to right and then down, where a w x h widget fits without
    // overlapping anything already placed.
    public static (int X, int Y) FindFreeSpot(IReadOnlyCollection<WidgetRect> placed, int w, int h)
    {
        int width = Math.Clamp(w, 1, Columns);
        int height = Math.Max(1, h);

        for (int row = 0; ; row++)
        {
            for (int column = 0; column <= Columns - width; column++)
            {
                WidgetRect candidate = new(Guid.Empty, column, row, width, height);

                if (!placed.Any(candidate.Overlaps))
                {
                    return (column, row);
                }
            }
        }
    }

    // Puts `anchor` in place, then repeatedly pushes whatever overlaps something already settled
    // down below it. The anchor never moves, so the dragged widget always lands where the pointer is.
    private static List<WidgetRect> Settle(IReadOnlyList<WidgetRect> layout, WidgetRect anchor, bool compact)
    {
        Dictionary<Guid, WidgetRect> rects = layout.ToDictionary(r => r.Id);
        rects[anchor.Id] = anchor;

        for (int pass = 0; pass < MaxSettlePasses; pass++)
        {
            bool moved = false;

            // Top-most first, so pushes ripple downwards in a predictable order. The anchor is
            // always treated as above everything it overlaps.
            List<WidgetRect> ordered = [.. rects.Values
                .OrderBy(r => r.Id == anchor.Id ? 0 : 1)
                .ThenBy(r => r.Y)
                .ThenBy(r => r.X)];

            for (int i = 0; i < ordered.Count && !moved; i++)
            {
                WidgetRect upper = rects[ordered[i].Id];

                for (int j = 0; j < ordered.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    WidgetRect lower = rects[ordered[j].Id];

                    if (lower.Id == anchor.Id || !upper.Overlaps(lower))
                    {
                        continue;
                    }

                    // Only push something that sits at or below the widget it collides with
                    // (or anything the anchor lands on), so two widgets never shove each other
                    // back and forth.
                    if (upper.Id != anchor.Id && lower.Y < upper.Y)
                    {
                        continue;
                    }

                    rects[lower.Id] = lower with { Y = upper.Bottom };
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        List<WidgetRect> result = [.. layout.Select(r => rects[r.Id])];

        if (!result.Any(r => r.Id == anchor.Id))
        {
            result.Add(anchor);
        }

        if (!compact)
        {
            return result;
        }

        // Compact everything, but never float the anchor away from where it was dropped: other
        // widgets settle around it. Compact the others with the anchor fixed in place.
        return CompactAround(result, anchor);
    }

    private static List<WidgetRect> CompactAround(List<WidgetRect> layout, WidgetRect anchor)
    {
        List<WidgetRect> ordered = [.. layout.Where(r => r.Id != anchor.Id).OrderBy(r => r.Y).ThenBy(r => r.X)];
        List<WidgetRect> settled = [anchor];

        foreach (WidgetRect rect in ordered)
        {
            WidgetRect current = rect;

            while (current.Y > 0)
            {
                WidgetRect up = current with { Y = current.Y - 1 };

                if (settled.Any(up.Overlaps))
                {
                    break;
                }

                current = up;
            }

            // Floating up can't jump over the anchor, but the anchor could have been dropped so
            // that this widget would now overlap it on the way up - the loop above already stops
            // on that, so `current` never overlaps anything settled.
            settled.Add(current);
        }

        Dictionary<Guid, WidgetRect> byId = settled.ToDictionary(r => r.Id);

        return [.. layout.Select(r => byId[r.Id])];
    }

    private static int IndexOf(IReadOnlyList<WidgetRect> layout, Guid id)
    {
        for (int i = 0; i < layout.Count; i++)
        {
            if (layout[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
