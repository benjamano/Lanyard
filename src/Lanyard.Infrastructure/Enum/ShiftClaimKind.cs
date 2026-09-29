namespace Lanyard.Infrastructure.Enum;

// What a ShiftClaim asks for. Stored, so never renumber.
public enum ShiftClaimKind
{
    // "I'll take it": someone asking for an open shift.
    Pickup = 0,

    // "I can't make it": the person on a shift asking to be taken off it. Always needs a manager.
    Drop = 1,

    // "Can anyone swap?": the person on a shift asking colleagues for offers.
    Swap = 2,

    // "I'll swap you for mine": a colleague's answer to a Swap, offering one of their own shifts.
    SwapOffer = 3
}
