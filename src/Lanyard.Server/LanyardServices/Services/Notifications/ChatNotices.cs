using Lanyard.Infrastructure.DTO.Notifications;

namespace Lanyard.Application.Services.Notifications;

// Wording for the chat notifications that aren't a single message: the daily unread email and
// report updates. Deliberately free of message text - a report notice doesn't say what was
// reported, and the digest names conversations and counts only.
public static class ChatNotices
{
    public const string ChatUrl = "/chat";
    public const string ReportsUrl = "/manage/chat/reports";

    public static bool Handles(NotificationPayload payload) =>
        payload is ChatDigestPayload or ChatReportedPayload or ChatReportReviewedPayload;

    public static Notice For(NotificationPayload payload) => payload switch
    {
        ChatDigestPayload digest => new Notice(
            digest.Lines.Sum(x => x.Count) == 1 ? "You have an unread message" : $"You have {digest.Lines.Sum(x => x.Count)} unread messages",
            [.. digest.Lines.Select(x => $"{x.ConversationName}: {x.Count} unread")],
            ChatUrl, "Open chat", "chat-digest"),

        ChatReportedPayload => new Notice(
            "A chat message was reported",
            ["Someone at your location has reported a message. Only that message is shared with you."],
            ReportsUrl, "Review it", "chat-report"),

        ChatReportReviewedPayload => new Notice(
            "Your report has been reviewed",
            ["A manager has looked at the message you reported. Thanks for letting us know."],
            ChatUrl, "Open chat", "chat-report-reviewed"),

        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "Not a chat notice.")
    };
}
