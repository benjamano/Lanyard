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
}
