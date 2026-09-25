namespace Lanyard.Infrastructure.Enum;

// What happened to someone's time off, for the email that tells them. Wider than TimeOffStatus
// because "rejected" reads very differently depending on whether it was ever approved.
public enum TimeOffEmailOutcome
{
    Approved = 0,

    // Was waiting for a decision and wasn't approved.
    Rejected = 1,

    // Was approved and hadn't started; the approval was taken back.
    Withdrawn = 2,

    // Was approved and already under way; the days from tomorrow were taken back.
    CutShort = 3,

    // A manager recorded it for them (sickness, say), approved straight away.
    Recorded = 4
}
