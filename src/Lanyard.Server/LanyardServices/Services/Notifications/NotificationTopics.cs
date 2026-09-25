using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

public enum NotificationTopicGroup
{
    Shifts,
    TimeOff
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
    bool ManagerOnly);

// The effective choice for one topic: the person's saved row, or the default.
public record TopicPreference(NotificationTopic Topic, bool Push, bool Email);

public static class NotificationTopics
{
    // Email stays on by default for every topic, so nobody loses the emails they got before push
    // existed; push is on by default too but only reaches devices where someone switched it on.
    public static readonly IReadOnlyList<NotificationTopicInfo> All =
    [
        new(NotificationTopic.RotaChanged, NotificationTopicGroup.Shifts,
            "Rota changes", "Your shifts are published, moved or removed.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.ShiftReminder, NotificationTopicGroup.Shifts,
            "Shift reminders", "A reminder before each shift you're working.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.TimeOffDecided, NotificationTopicGroup.TimeOff,
            "Time off decisions", "Your time off is approved, rejected or changed by a manager.", DefaultPush: true, DefaultEmail: true, ManagerOnly: false),
        new(NotificationTopic.TimeOffRequested, NotificationTopicGroup.TimeOff,
            "Time off requests", "Someone at your location asks for time off.", DefaultPush: true, DefaultEmail: true, ManagerOnly: true)
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
        NotificationTopicGroup.TimeOff => "Time off",
        _ => group.ToString()
    };
}
