using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.App.Components.WhatsOn;

// Where each entry sits on the Day timeline: minutes past venue-local midnight, and which of the
// side-by-side lanes it takes when entries overlap. Pure so it can be tested without rendering.
public static class TimelineLayout
{
    // The hours always shown, so a quiet day still looks like a day and two days line up.
    public const int DefaultStartHour = 9;
    public const int DefaultEndHour = 18;

    // A very short entry still needs room for its title.
    public const int MinimumMinutes = 30;

    public record Window(int StartHour, int EndHour)
    {
        public int StartMinute => StartHour * 60;
        public int EndMinute => EndHour * 60;
        public int TotalMinutes => EndMinute - StartMinute;

        public IEnumerable<int> Hours => Enumerable.Range(StartHour, EndHour - StartHour + 1);

        // Percentage down the timeline, for CSS top/height.
        public double Percent(int minute) => Math.Clamp((minute - StartMinute) * 100.0 / TotalMinutes, 0, 100);
    }

    public record Block(WhatsOnItem Item, int StartMinute, int EndMinute, int Lane, int Lanes);

    // Minutes past local midnight on day; an entry running past midnight stops at 24:00.
    public static (int Start, int End) Minutes(WhatsOnItem item, DateOnly day)
    {
        int start = MinutesOn(item.StartUtc, day);
        int end = RotaTime.LocalDate(item.EndUtc) > day ? 24 * 60 : MinutesOn(item.EndUtc, day);

        return (start, Math.Min(24 * 60, Math.Max(end, start + MinimumMinutes)));
    }

    public static int MinutesOn(DateTime utc, DateOnly day)
    {
        DateTime local = RotaTime.ToVenueLocal(utc);

        return local.Date < day.ToDateTime(TimeOnly.MinValue) ? 0 : local.Hour * 60 + local.Minute;
    }

    // The default hours, stretched to whole hours to fit everything on the day.
    public static Window WindowFor(IEnumerable<WhatsOnItem> items, DateOnly day)
    {
        int start = DefaultStartHour;
        int end = DefaultEndHour;

        foreach (WhatsOnItem item in items)
        {
            (int s, int e) = Minutes(item, day);
            start = Math.Min(start, s / 60);
            end = Math.Max(end, (e + 59) / 60);
        }

        return new Window(start, Math.Min(24, end));
    }

    // Entries that overlap form a cluster; each takes the first lane free at its start, and every
    // entry in the cluster is drawn the same width (1 / the lanes the cluster needed).
    public static List<Block> Layout(IEnumerable<WhatsOnItem> items, DateOnly day)
    {
        List<(WhatsOnItem Item, int Start, int End)> sorted = items
            .Select(x => { (int s, int e) = Minutes(x, day); return (x, s, e); })
            .OrderBy(x => x.s)
            .ThenByDescending(x => x.e)
            .ToList();

        List<Block> result = [];
        List<(WhatsOnItem Item, int Start, int End, int Lane)> cluster = [];
        List<int> laneEnds = [];
        int clusterEnd = int.MinValue;

        void Flush()
        {
            result.AddRange(cluster.Select(x => new Block(x.Item, x.Start, x.End, x.Lane, laneEnds.Count)));
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach ((WhatsOnItem item, int start, int end) in sorted)
        {
            if (cluster.Count > 0 && start >= clusterEnd)
            {
                Flush();
            }

            int lane = laneEnds.FindIndex(x => x <= start);

            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            else
            {
                laneEnds[lane] = end;
            }

            cluster.Add((item, start, end, lane));
            clusterEnd = cluster.Count == 1 ? end : Math.Max(clusterEnd, end);
        }

        Flush();

        return result;
    }
}
