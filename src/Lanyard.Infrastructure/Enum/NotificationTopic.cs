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
    ChatReportReviewed = 12,
    ChannelMessage = 13,
    PinnedPost = 14,
    Announcement = 15,
    KioskOffline = 16,
    KioskNeedsAttention = 17,

    // 18 is kept for PolicyAcknowledgement (open PR #294).

    AutomationRuleFailed = 19,
    MissedClockIn = 20,
    TrainingOverdue = 21,
    StaffDocumentExpiring = 22,
    PartyBookingImportFailed = 23,

    // A location checklist (Opening, Closing...) was missed. Raised as an operations alert.
    ChecklistMissed = 24,

    // 25 is kept for checklist readings out of range (item 10.2).

    // A party change left a day's built rota with the wrong party staffing. Raised as an operations alert.
    PartyStaffingOutOfDate = 26
}
