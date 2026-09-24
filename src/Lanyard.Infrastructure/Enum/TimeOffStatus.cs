namespace Lanyard.Infrastructure.Enum;

// A time-off request is never deleted: a cancelled or rejected one stays as the record of what was
// asked for and why it didn't happen.
public enum TimeOffStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3
}
