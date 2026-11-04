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
    Voucher,
    FeedbackForm,
    Pack
}

// The printouts drawn on a company's own artwork. Stored as ints - never renumber.
public enum PartyArtworkDocument
{
    Voucher = 0,
    FeedbackForm = 1
}

// What goes in a box on the artwork.
public enum PartyTemplateFieldKind
{
    PresentedTo,
    PartyDate,
    ExpiryDate,
    AuthorisedBy,
    PartyName,
    PartyHost,
    ChildAge
}

public enum PartyTemplateFont
{
    Standard = 0,

    // Chewy, the rounded hand-lettered face Play2Day's artwork is set in.
    Handwritten = 1
}

public enum PartyTextAlign
{
    Left,
    Centre
}
