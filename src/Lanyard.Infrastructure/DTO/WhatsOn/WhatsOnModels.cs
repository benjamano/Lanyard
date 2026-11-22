using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.WhatsOn;

// What a "What's on" entry came from. Parties today; ordinary bookings (soft play sessions,
// laser tag slots) will be another source later. Not stored, so free to grow.
public enum WhatsOnKind
{
    Party = 0
}

// A labelled moment inside an entry, drawn as a tick on the timeline: "Eat 11:00", "Laser 10:30".
public record WhatsOnMarker(string Label, DateTime AtUtc);

// One thing happening at the venue, in a shape that doesn't care where it came from, so the
// What's on page and widget can show parties and (later) bookings side by side.
public record WhatsOnItem
{
    public required WhatsOnKind Kind { get; init; }
    public required Guid Id { get; init; }

    // "Ava, turning 7" / "Private hire · Sam Jones".
    public required string Title { get; init; }

    // "Play + Laser" / "Laser tag + Play".
    public string? Subtitle { get; init; }

    // Where clicking the entry goes.
    public string? Href { get; init; }

    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }

    public List<WhatsOnMarker> Markers { get; init; } = [];

    public int Children { get; init; }
    public int? Adults { get; init; }

    // True for a private hire, where Children counts guests of any age.
    public bool CountsGuests { get; init; }

    public string? Room { get; init; }
    public string? HostName { get; init; }
    public string? AllergyNotes { get; init; }

    public bool IsProvisional { get; init; }
    public bool IsCancelled { get; init; }

    public PartyPaymentStatus? PaymentStatus { get; init; }
    public decimal? BalanceDue { get; init; }

    public PartyMenuType? MenuType { get; init; }

    public DateOnly LocalDate => RotaTime.LocalDate(StartUtc);
}

// Someone on the published rota that day.
public record WhatsOnStaff(string UserId, string Name, string? Position, int? PositionColorIndex, DateTime StartUtc, DateTime EndUtc);

public record WhatsOnTotals(int Items, int Children, int Adults, int HotFood, int ColdFood, int Unpaid, decimal BalanceDue, int Provisional)
{
    public static WhatsOnTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0m, 0);

    // Cancelled entries are listed (when asked for) but never counted.
    public static WhatsOnTotals For(IEnumerable<WhatsOnItem> items)
    {
        List<WhatsOnItem> live = items.Where(x => !x.IsCancelled).ToList();

        return new WhatsOnTotals(
            Items: live.Count,
            Children: live.Sum(x => x.Children),
            Adults: live.Sum(x => x.Adults ?? 0),
            HotFood: live.Count(x => x.MenuType == PartyMenuType.Hot),
            ColdFood: live.Count(x => x.MenuType == PartyMenuType.Cold),
            // Not paid in full yet: unpaid, or only the deposit in.
            Unpaid: live.Count(x => x.PaymentStatus is PartyPaymentStatus.Unpaid or PartyPaymentStatus.DepositPaid),
            BalanceDue: live.Sum(x => x.BalanceDue ?? 0m),
            Provisional: live.Count(x => x.IsProvisional));
    }

    public static WhatsOnTotals Sum(IEnumerable<WhatsOnTotals> days)
    {
        List<WhatsOnTotals> list = days.ToList();

        return new WhatsOnTotals(
            list.Sum(x => x.Items), list.Sum(x => x.Children), list.Sum(x => x.Adults),
            list.Sum(x => x.HotFood), list.Sum(x => x.ColdFood), list.Sum(x => x.Unpaid),
            list.Sum(x => x.BalanceDue), list.Sum(x => x.Provisional));
    }
}

public record WhatsOnDay(DateOnly Date, List<WhatsOnItem> Items, List<WhatsOnStaff> Staff, WhatsOnTotals Totals);

// Every venue-local day from From to To inclusive, empty days included. Warnings carry a source
// that failed to load, so the rest of the page still shows.
public record WhatsOnRange(int LocationId, DateOnly From, DateOnly To, List<WhatsOnDay> Days, List<string> Warnings)
{
    public WhatsOnTotals Totals => WhatsOnTotals.Sum(Days.Select(x => x.Totals));

    public WhatsOnDay? Day(DateOnly date) => Days.FirstOrDefault(x => x.Date == date);
}
