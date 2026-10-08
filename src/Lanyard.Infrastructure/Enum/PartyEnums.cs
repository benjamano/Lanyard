namespace Lanyard.Infrastructure.Enum;

// Stored as ints - never renumber.
public enum PartyBookingStatus
{
    Booked = 0,
    Cancelled = 1
}

// Which food sheet the party gets: the hot meal tally sheet or the cold sandwich sheet.
public enum PartyMenuType
{
    Hot = 0,
    Cold = 1
}

// Worked out from the booking's payment dates, not stored.
public enum PartyPaymentStatus
{
    Unpaid,
    DepositPaid,
    PaidInFull
}
