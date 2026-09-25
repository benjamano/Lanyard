using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

public enum NotificationTopicGroup
{
    Shifts,
    Cover,
    TimeOff,
    Chat
}

// How one topic is described in Notification settings, and what someone gets before they have
// chosen anything. ManagerOnly topics are only ever sent to Managers/Admins, so the settings card
// hides them from everyone else.
public record NotificationTopicInfo(
    NotificationTopic Topic,
    NotificationTopicGroup Group,
    string Label,
    string Description,
    bool DefaultPush,
    bool DefaultEmail,
    bool ManagerOnly,
    bool PushAvailable = true,
    bool EmailAvailable = true);

// The effective choice for one topic: the person's saved row, or the default.
public record TopicPreference(NotificationTopic Topic, bool Push, bool Email);

public static class NotificationTopics
{
    // Email stays on by default for every topic that emailed before push existed, so nobody loses
    // those. Open shifts, swaps and requests to review are frequent and only matter for a few
    // hours, so they are push-only unless someone opts in to email. Push is on by default but
    // only reaches devices where someone switched it on.
    public static readonly IReadOnlyList<NotificationTopicInfo> All =
    [
        new(NotificationTopic.RotaChanged, NotificationTopicGroup.Shifts,
            "Rota changes", "Your shifts are published, moved or removed.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.ShiftReminder, NotificationTopicGroup.Shifts,
            "Shift reminders", "A reminder before each shift you're working.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.OpenShift, NotificationTopicGroup.Cover,
            "Open shifts", "A shift you could work is up for grabs.", DefaultPush: true, DefaultEmail: false, ManagerOnly: false),
        new(NotificationTopic.SwapRequest, NotificationTopicGroup.Cover,
            "Swaps", "A colleague is looking for a swap, or has offered you one.", DefaultPush: true, DefaultEmail: false, ManagerOnly: false),
        new(NotificationTopic.ShiftClaimDecided, NotificationTopicGroup.Cover,
            "Pick-up, call-off and swap decisions", "Whether you got a shift, were let off one, or your swap went ahead.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.ShiftClaimPending, NotificationTopicGroup.Cover,
            "Shift requests to review", "Someone at your location wants to pick up, call off or swap a shift.", DefaultPush: true, DefaultEmail: false, ManagerOnly: true),
        new(NotificationTopic.TimeOffDecided, NotificationTopicGroup.TimeOff,
            "Time off decisions", "Your time off is approved, rejected or changed by a manager.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.TimeOffRequested, NotificationTopicGroup.TimeOff,
            "Time off requests", "Someone at your location asks for time off.", DefaultPush: true, DefaultEmail: true, ManagerOnly: true),

        // Chat messages come by push as they arrive; the email is one daily summary instead of one
        // per message, so each is a single-channel topic.
        new(NotificationTopic.DirectMessage, NotificationTopicGroup.Chat,
            "Direct messages", "Someone sends you a message.", DefaultPush: true, DefaultEmail: false, ManagerOnly: false, EmailAvailable: false),
        new(NotificationTopic.GroupMessage, NotificationTopicGroup.Chat,
            "Group messages", "A new message in one of your groups.", DefaultPush: true, DefaultEmail: false, ManagerOnly: false, EmailAvailable: false),
        new(NotificationTopic.ChannelMessage, NotificationTopicGroup.Chat,
            "Channel messages", "A new message in your location or company channel.", DefaultPush: false, DefaultEmail: false, ManagerOnly: false, EmailAvailable: false),
        new(NotificationTopic.PinnedPost, NotificationTopicGroup.Chat,
            "Pinned posts", "Your managers pin a post in one of your channels.", DefaultPush: true, DefaultEmail: false, ManagerOnly: false),
        new(NotificationTopic.ChatUnreadEmail, NotificationTopicGroup.Chat,
            "Unread messages email", "A daily email when messages have been waiting for you for a day.", DefaultPush: false, DefaultEmail: true, ManagerOnly: false, PushAvailable: false),
        new(NotificationTopic.ChatReportReviewed, NotificationTopicGroup.Chat,
            "Your reports", "A manager has reviewed a message you reported.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.ChatReport, NotificationTopicGroup.Chat,
            "Reported messages", "Someone at your location reports a chat message.", DefaultPush: true, DefaultEmail: true, ManagerOnly: true)
    ];

    public static NotificationTopicInfo Get(NotificationTopic topic) =>
        All.FirstOrDefault(x => x.Topic == topic)
        ?? throw new ArgumentOutOfRangeException(nameof(topic), topic, "Every topic needs an entry in NotificationTopics.All.");

    public static TopicPreference Default(NotificationTopic topic)
    {
        NotificationTopicInfo info = Get(topic);

        return new TopicPreference(topic, info.DefaultPush, info.DefaultEmail);
    }

    public static string GroupLabel(NotificationTopicGroup group) => group switch
    {
        NotificationTopicGroup.Shifts => "Shifts",
        NotificationTopicGroup.Cover => "Cover and swaps",
        NotificationTopicGroup.TimeOff => "Time off",
        NotificationTopicGroup.Chat => "Chat",
        _ => group.ToString()
    };
}
