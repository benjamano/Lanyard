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

// What a menu item is, which decides where it prints. Stored as ints - never renumber.
public enum PartyMenuItemKind
{
    // A hot meal: a block on the hot food sheet, with its allergens printed under it.
    HotMain = 0,

    // A side or vegetable choice offered with every hot meal (peas, beans, sweetcorn).
    HotSide = 1,

    // A sandwich filling on the cold food sheet.
    Sandwich = 2,

    // Everything else in a cold party box (crisps, jelly, raisins, bread). Printed only in the
    // cold sheet's allergy panel.
    ColdExtra = 3
}

// The printouts a party can produce. Pack is all of them in one PDF, for a single print.
public enum PartyDocumentType
{
    ReservedCard,
    FoodSheet,
    Pack
}
