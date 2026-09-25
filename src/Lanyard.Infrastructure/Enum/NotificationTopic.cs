namespace Lanyard.Infrastructure.Enum;

// What a notification is about. Each topic can go by push, by email, or both, per person
// (NotificationPreference); the defaults and labels live in NotificationTopics. Values are stored,
// so never renumber them.
public enum NotificationTopic
{
    RotaChanged = 0,
    ShiftReminder = 1,
    TimeOffRequested = 2,
    TimeOffDecided = 3
}
