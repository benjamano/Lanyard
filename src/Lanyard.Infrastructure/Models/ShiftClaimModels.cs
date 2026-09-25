using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A request to change who works a shift: picking up an open shift, calling off your own, or
    // swapping. One row per person per request; a swap is one Swap row from the person on the shift
    // plus one SwapOffer row per colleague who answers it (ParentClaimId -> the Swap).
    //   Pickup    ShiftId = the open shift,       UserId = who wants it
    //   Drop      ShiftId = their shift,          UserId = who can't make it
    //   Swap      ShiftId = their shift,          UserId = who wants to swap it away
    //   SwapOffer ShiftId = the Swap's shift,     UserId = who'd take it,
    //             OfferedShiftId = the shift they give in return
    // Nothing is ever deleted: the history of who covered for whom stays with the shifts, and goes
    // with them (anonymised) under the same retention rules.
    public class ShiftClaim
    {
        public Guid Id { get; set; }

        public Guid ShiftId { get; set; }
        public Shift? Shift { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public ShiftClaimKind Kind { get; set; }

        public Guid? ParentClaimId { get; set; }
        public ShiftClaim? ParentClaim { get; set; }

        public Guid? OfferedShiftId { get; set; }
        public Shift? OfferedShift { get; set; }

        public ShiftClaimStatus Status { get; set; } = ShiftClaimStatus.Pending;

        // What the person said when asking ("family emergency", "happy to do a late").
        public string? Note { get; set; }

        public DateTime RequestedUtc { get; set; }

        public string? DecidedByUserId { get; set; }
        public DateTime? DecidedUtc { get; set; }

        // Why it was turned down or withdrawn, in words the person can read.
        public string? DecisionReason { get; set; }

        public bool IsOpen => Status is ShiftClaimStatus.Pending or ShiftClaimStatus.Accepted;
    }

    // Per-location rota rules. One row per location, created on first save; no row = the defaults.
    public class LocationSchedulingSettings
    {
        public Guid Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        // On: picking up an open shift and swapping wait for a manager. Off: the first person to
        // pick up an open shift gets it, and a swap happens as soon as both people agree.
        // Calling off a shift always needs a manager.
        public bool ClaimsNeedApproval { get; set; } = true;

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }
}
