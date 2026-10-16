namespace Lanyard.Infrastructure.Enum;

// Why a person is, or might not be, a good pick for a shift (see ShiftSuggestionRules).
public enum SuggestionReasonKind
{
    // Positives
    NeedsShifts = 0,
    BelowMinHours = 1,
    AskedForShift = 2,
    UsualPosition = 3,

    // Cautions
    OverMaxHours = 10,
    PendingTimeOff = 11,
    ShortRest = 12
}
