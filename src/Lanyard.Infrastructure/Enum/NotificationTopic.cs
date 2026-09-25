namespace Lanyard.Infrastructure.Enum;

// What a notification is about. Each topic can go by push, by email, or both, per person
// (NotificationPreference); the defaults and labels live in NotificationTopics. Values are stored,
// so never renumber them.
public enum NotificationTopic
{
    RotaChanged = 0,
    ShiftReminder = 1,
    TimeOffRequested = 2,
    TimeOffDecided = 3,
    OpenShift = 4,
    SwapRequest = 5,
    ShiftClaimDecided = 6,
    ShiftClaimPending = 7,
    DirectMessage = 8,
    GroupMessage = 9,
    ChatUnreadEmail = 10,
    ChatReport = 11,
    ChatReportReviewed = 12
}
