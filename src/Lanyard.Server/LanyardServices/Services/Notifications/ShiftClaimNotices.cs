using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

// One notification in words: a title (email subject / push title), a few short lines (email
// paragraphs / push body), and where the button or tap goes. Tag groups pushes on the phone.
public record Notice(string Title, List<string> Lines, string Url, string ButtonLabel, string Tag);

// Wording for open shifts, swaps and call-offs, shared by the email and the push so the two
// always say the same thing.
public static class ShiftClaimNotices
{
    public const string MyShiftsUrl = "/rota";
    public const string UpForGrabsUrl = "/rota/open";
    public const string ReviewUrl = "/manage/rota/requests";

    public static bool Handles(NotificationPayload payload) => payload is OpenShiftPayload or SwapRequestedPayload
        or SwapOfferedPayload or ShiftClaimPendingPayload or ShiftClaimDecidedPayload;

    public static Notice For(NotificationPayload payload) => payload switch
    {
        OpenShiftPayload open => new Notice(
            $"Shift up for grabs at {open.LocationName}",
            [Line(open.Shift), open.NeedsApproval ? "Ask for it in Lanyard; a manager will confirm who gets it." : "First to pick it up in Lanyard gets it."],
            UpForGrabsUrl, "See open shifts", $"open-{open.Shift.Date:yyyyMMdd}-{open.Shift.TimeRange}"),

        SwapRequestedPayload swap => new Notice(
            $"{swap.RequesterName} is looking for a swap",
            [Line(swap.Shift), .. Quote(swap.Note), "Offer one of your shifts in return in Lanyard."],
            UpForGrabsUrl, "Offer a swap", $"swap-{swap.RequesterName}-{swap.Shift.Date:yyyyMMdd}"),

        SwapOfferedPayload offer => new Notice(
            $"{offer.OffererName} offered you a swap",
            [$"They'd take your {Line(offer.YourShift)}", $"and you'd work their {Line(offer.TheirShift)}"],
            UpForGrabsUrl, "Choose an offer", $"swap-offer-{offer.YourShift.Date:yyyyMMdd}-{offer.OffererName}"),

        ShiftClaimPendingPayload pending => Pending(pending),

        ShiftClaimDecidedPayload decided => Decided(decided),

        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "Not a shift claim notification.")
    };

    private static Notice Pending(ShiftClaimPendingPayload p)
    {
        (string title, List<string> lines) = p.Kind switch
        {
            ShiftClaimKind.Pickup => ($"{p.PersonName} wants to pick up a shift", new List<string> { Line(p.Shift) }),
            ShiftClaimKind.Drop => ($"{p.PersonName} can't make a shift", new List<string> { Line(p.Shift) }),
            _ => ($"{p.PersonName} and {p.OtherPersonName} want to swap",
                  new List<string> { $"{p.PersonName}: {Line(p.Shift)}", $"{p.OtherPersonName}: {(p.OtherShift is null ? "" : Line(p.OtherShift))}" })
        };

        return new Notice(title, [.. lines, .. Quote(p.Note)], ReviewUrl, "Review it", $"claim-{p.Kind}-{p.PersonName}-{p.Shift.Date:yyyyMMdd}");
    }

    private static Notice Decided(ShiftClaimDecidedPayload d)
    {
        string title = (d.Kind, d.Approved) switch
        {
            (ShiftClaimKind.Pickup, true) => "The shift is yours",
            (ShiftClaimKind.Pickup, false) => "You didn't get the shift",
            (ShiftClaimKind.Drop, true) => "You're off the shift",
            (ShiftClaimKind.Drop, false) => "You're still on the shift",
            (_, true) => "Your swap is done",
            (_, false) => "Your swap didn't go ahead"
        };

        List<string> lines = (d.Kind, d.Approved) switch
        {
            (ShiftClaimKind.Swap or ShiftClaimKind.SwapOffer, true) when d.OtherShift is not null =>
                [$"You now work {Line(d.Shift)}", $"instead of {Line(d.OtherShift)}"],
            (ShiftClaimKind.Drop, true) => [$"{Line(d.Shift)} is now open for someone else to pick up."],
            _ => [Line(d.Shift)]
        };

        return new Notice(title, [.. lines, .. Quote(d.Reason)], MyShiftsUrl, "See my shifts", $"claim-decided-{d.Shift.Date:yyyyMMdd}-{d.Kind}");
    }

    public static string Line(ShiftEmailLine line)
    {
        string text = $"{line.Date.ToString("ddd d MMM", RotaFormat.Uk)} · {line.TimeRange}";

        return line.PositionName is { Length: > 0 } position ? $"{text} · {position}" : text;
    }

    private static IEnumerable<string> Quote(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [$"“{text.Trim()}”"];
}
