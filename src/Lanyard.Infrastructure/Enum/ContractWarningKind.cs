namespace Lanyard.Infrastructure.Enum;

public enum ContractWarningKind
{
    BelowMinShifts = 0,
    BelowMinHours = 1,
    AboveMaxHours = 2,

    // A shift sits on a day the person has approved time off.
    ShiftDuringTimeOff = 3,

    // A young worker limit broken that week: hours, breaks, rest, night work, or rostering someone
    // under school leaving age (YoungWorkerRules). The message says which.
    YoungWorkerLimit = 4
}
