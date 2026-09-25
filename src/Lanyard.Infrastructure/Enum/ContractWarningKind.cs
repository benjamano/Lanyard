namespace Lanyard.Infrastructure.Enum;

public enum ContractWarningKind
{
    BelowMinShifts = 0,
    BelowMinHours = 1,
    AboveMaxHours = 2,

    // A shift sits on a day the person has approved time off.
    ShiftDuringTimeOff = 3
}
