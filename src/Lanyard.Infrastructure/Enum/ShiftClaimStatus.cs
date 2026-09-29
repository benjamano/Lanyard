namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
public enum ShiftClaimStatus
{
    // Waiting: for a manager (pickup, drop), or for offers (swap) / the requester's choice (offer).
    Pending = 0,

    // A swap whose requester has picked an offer, waiting for a manager at a location that needs
    // one. The chosen offer is Accepted too.
    Accepted = 1,

    // Done: the shift has changed hands.
    Approved = 2,

    // Turned down by a manager, or another claim on the same shift won.
    Rejected = 3,

    // Called off by the person who made it, or overtaken by the shift changing.
    Withdrawn = 4
}
