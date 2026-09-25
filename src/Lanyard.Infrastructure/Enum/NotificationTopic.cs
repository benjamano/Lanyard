namespace Lanyard.Infrastructure.Enum;

// What a notification is about. Email only for now; per-person preferences and push (which
// choose per topic) build on this list.
public enum NotificationTopic
{
    RotaChanged = 0,
    ShiftReminder = 1,
    TimeOffRequested = 2,
    TimeOffDecided = 3
}
