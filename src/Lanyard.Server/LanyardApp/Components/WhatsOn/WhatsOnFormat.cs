using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.DTO.WhatsOn;

namespace Lanyard.App.Components.WhatsOn;

// Text shared by the What's on page, week grid and widget.
public static class WhatsOnFormat
{
    public static string TimeRange(this WhatsOnItem item) => RotaFormat.TimeRange(item.StartUtc, item.EndUtc);

    // "18 children · 6 adults" / "40 guests".
    public static string Headcount(this WhatsOnItem item)
    {
        string noun = item.CountsGuests
            ? item.Children == 1 ? "guest" : "guests"
            : item.Children == 1 ? "child" : "children";

        string people = $"{item.Children} {noun}";

        return item.Adults is int adults ? $"{people} · {adults} {(adults == 1 ? "adult" : "adults")}" : people;
    }

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
