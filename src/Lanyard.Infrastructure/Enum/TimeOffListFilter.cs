namespace Lanyard.Infrastructure.Enum;

// The manager's time-off list views.
public enum TimeOffListFilter
{
    // Waiting for a decision, soonest first.
    Pending = 0,

    // Approved and not yet over, soonest first.
    Upcoming = 1,

    // Everything that started in the last 90 days or is still to come, newest first.
    Recent = 2
}
