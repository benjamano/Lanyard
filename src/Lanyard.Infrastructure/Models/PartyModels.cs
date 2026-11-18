using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A kids' party booked at a location. Free-form on purpose: there are no packages, the
    // manager just types in what was agreed. The fields mirror the venue's paper weekend party
    // sheet and the header of the food sheets, which are generated from this row.
    //
    // Every instant is UTC and read back in venue-local time through RotaTime, the same as Shift.
    // Room, LaserTagTimeUtc and PartyHostUserId are what the future staff day-timeline will read to
    // put the party room, laser tag slot and host on the plan for the day.
    //
    // Holds a parent's contact details and a child's name, so it's personal data about people
    // who aren't Lanyard users.
    public class PartyBooking : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        // Arrival and finish.
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        public DateTime? EatTimeUtc { get; set; }
        public DateTime? LaserTagTimeUtc { get; set; }

        // Free text, e.g. "Play", "Lazer + Play", "Play + Inflatable". The form suggests values
        // used before so the list stays consistent without anyone having to maintain it.
        public string PartyType { get; set; } = string.Empty;

        public string ChildName { get; set; } = string.Empty;
        public int? ChildAgeTurning { get; set; }

        public string ContactName { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
        public string? ContactEmail { get; set; }

        public int ExpectedChildren { get; set; }
        public int? ExpectedAdults { get; set; }

        public string? Room { get; set; }

        public PartyMenuType MenuType { get; set; }

        public string? PartyHostUserId { get; set; }
        public UserProfile? PartyHost { get; set; }

        public string? AllergyNotes { get; set; }
        public string? Notes { get; set; }

        public decimal? TotalPrice { get; set; }
        public decimal? DepositAmount { get; set; }
        public DateTime? DepositPaidUtc { get; set; }
        public DateTime? BalancePaidUtc { get; set; }
        public string? PaymentNotes { get; set; }

        // Set when the whole venue (or the play area / laser tag) is hired out rather than a kids'
        // party being booked: the child, food and printout fields don't apply, and Party staffing
        // has its own lines for each kind of hire.
        public PrivateHireKind? HireKind { get; set; }

        public bool IsPrivateHire => HireKind is not null;

        // "Ava's party" / "Private hire: Laser tag + Play".
        public string DisplayName => HireKind is PrivateHireKind kind
            ? $"Private hire: {kind.Label()}"
            : string.IsNullOrWhiteSpace(ChildName) ? "Party" : $"{ChildName}'s party";

        // Cancelling is the soft delete: a cancelled party stays on record and can be reinstated.
        public PartyBookingStatus Status { get; set; }

        public DateTime CreateDate { get; set; }
        public string CreateByUserId { get; set; } = string.Empty;
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }

        public PartyPaymentStatus PaymentStatus =>
            BalancePaidUtc is not null ? PartyPaymentStatus.PaidInFull
            : DepositPaidUtc is not null ? PartyPaymentStatus.DepositPaid
            : PartyPaymentStatus.Unpaid;

        public decimal? BalanceDue =>
            BalancePaidUtc is not null ? 0m
            : TotalPrice is decimal total ? Math.Max(0m, total - (DepositPaidUtc is not null ? DepositAmount ?? 0m : 0m)) : null;
    }

    // How many extra staff a kind of party needs on the rota: "a Lazer + Play party needs 2 more
    // Party Hosts". They work the whole template shift the party falls in (09:00-14:00 for a 10:00
    // party) and do the party as part of it; Auto-build adds them for every booked party. PartyType is matched to the booking's free-text type
    // ignoring case and spaces at either end; null is the rule for any type that has no rules of
    // its own. A type can have several rules, one per position. Archived (IsActive = false) rather
    // than deleted, like the rest of the rota setup.
    public class PartyStaffingRule : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public string? PartyType { get; set; }

        // Set for a private hire's staffing (PartyType is then null). Hires only use these lines, and
        // a kids' party never does.
        public PrivateHireKind? HireKind { get; set; }

        public required Guid StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public int StaffCount { get; set; } = 1;

        public bool IsActive { get; set; } = true;

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }

        public bool Matches(string? partyType) =>
            string.Equals(Normalise(PartyType), Normalise(partyType), StringComparison.OrdinalIgnoreCase);

        public static string? Normalise(string? partyType) =>
            string.IsNullOrWhiteSpace(partyType) ? null : partyType.Trim();

        // The rules that apply to a booking: a private hire's lines for that kind of hire, or a kids'
        // party's lines for its type (or the default ones).
        public static List<PartyStaffingRule> For(IEnumerable<PartyStaffingRule> rules, PartyBooking booking) =>
            booking.HireKind is PrivateHireKind kind
                ? rules.Where(x => x.IsActive && x.HireKind == kind).ToList()
                : For(rules, booking.PartyType);

        // The rules that apply to a kids' party of this type: its own, or the default ones if it has none.
        public static List<PartyStaffingRule> For(IEnumerable<PartyStaffingRule> rules, string? partyType)
        {
            List<PartyStaffingRule> active = rules.Where(x => x.IsActive && x.HireKind is null).ToList();
            List<PartyStaffingRule> own = Normalise(partyType) is null ? [] : active.Where(x => x.PartyType is not null && x.Matches(partyType)).ToList();

            return own.Count > 0 ? own : active.Where(x => x.PartyType is null).ToList();
        }
    }

    // Per-location party rules. One row per location, created on first save; no row = the defaults.
    public class PartyLocationSettings
    {
        public Guid Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        // The booking form fills these in from the arrival time, so the manager only types the
        // arrival. Null offsets leave that box empty. A time the manager changes by hand is left
        // alone after that, and editing an existing party never moves its times.
        public int DefaultLengthMinutes { get; set; } = 120;
        public int? EatTimeOffsetMinutes { get; set; }
        public int? LaserTagOffsetMinutes { get; set; }

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }

    // One line of a location's party menu. The food sheets are printed from these, so changing an
    // allergen here changes the next sheet printed - nothing is copied onto bookings.
    public class PartyMenuItem
    {
        public Guid Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public PartyMenuItemKind Kind { get; set; }

        public string Name { get; set; } = string.Empty;

        // Printed next to the name, e.g. "with chips". Hot meals only.
        public string? Description { get; set; }

        // e.g. "Contains: Gluten, Wheat, Soya". Printed on the food sheets as written.
        public string? AllergenText { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }
    }

    // A company's own artwork for a printout (voucher or feedback form), with where to print each
    // value on it. One row per company per document; no row = Lanyard's standard design. Company-
    // wide rather than per location: it's the company's branding, the same at every venue.
    public class PartyDocumentTemplate : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public PartyArtworkDocument Document { get; set; }

        // A PNG or JPEG of the blank artwork (boxes empty), filling the page. QuestPDF can't draw a
        // PDF onto a page, so the artwork has to be an image. Null = the standard design.
        public Guid? ArtworkFileId { get; set; }

        // The artwork's printed size. The voucher and feedback form are A5 portrait at Play2Day; the
        // reserved table card starts as A5 landscape.
        public decimal WidthMm { get; set; } = 148;
        public decimal HeightMm { get; set; } = 210;

        public PartyTemplateFont Font { get; set; } = PartyTemplateFont.Handwritten;

        // JSON list of PartyTemplateField - the boxes on the artwork. See PartyTemplateFields.
        public string FieldsJson { get; set; } = "[]";

        // Vouchers only: how long the free return is valid from the party date.
        public int VoucherValidityMonths { get; set; } = 12;

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }
}
