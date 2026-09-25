using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lib.Net.Http.WebPush;

namespace Lanyard.Application.Services.Notifications;

// One push notification as the service worker shows it. Tag groups notifications on the phone:
// a newer one with the same tag replaces the older one instead of stacking up.
public record PushContent(string Title, string Body, string Url, string Tag, PushMessageUrgency Urgency, TimeSpan TimeToLive);

// The push wording for each notification payload. Kept short enough for a lock screen, and in
// the same words as the matching email subject so the two read as one message.
public static class PushContentBuilder
{
    public static PushContent Build(NotificationPayload payload, DateOnly today) => payload switch
    {
        RotaChangedPayload rota => Rota(rota),
        ShiftReminderPayload reminder => Reminder(reminder, today),
        TimeOffRequestedPayload requested => new PushContent(
            $"{requested.RequesterName} has asked for time off",
            $"{requested.TypeName} · {TimeOffFormat.DateRange(requested.Start, requested.End)} · {requested.Amount}",
            "/manage/rota/time-off",
            // One per request: separate requests must not replace each other on the manager's phone.
            $"time-off-request-{requested.LocationId}-{requested.Start:yyyyMMdd}-{requested.End:yyyyMMdd}-{requested.RequesterName}",
            PushMessageUrgency.Normal,
            TimeSpan.FromDays(3)),
        TimeOffDecidedPayload decided => Decided(decided),
        _ when ShiftClaimNotices.Handles(payload) => FromNotice(payload),
        ChatMessagePayload chat => ChatMessage(chat),
        PinnedPostPayload pinned => new PushContent($"📌 {pinned.ChannelName}", $"{pinned.AuthorName}: {pinned.Preview}",
            $"/chat/{pinned.ConversationId}", $"pinned-{pinned.ConversationId:N}", PushMessageUrgency.Normal, TimeSpan.FromDays(3)),
        _ when ChatNotices.Handles(payload) => FromChatNotice(payload),
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.GetType().Name, "No push wording for this payload.")
    };

    private static PushContent Rota(RotaChangedPayload rota)
    {
        bool onlyNew = rota.Changed.Count == 0 && rota.Removed.Count == 0;
        string title = onlyNew ? $"New shifts at {rota.LocationName}" : $"Your rota at {rota.LocationName} has changed";

        List<string> parts = [];

        if (rota.Added.Count > 0)
        {
            parts.Add(Count(rota.Added.Count, "new shift", "new shifts"));
        }

        if (rota.Changed.Count > 0)
        {
            parts.Add(Count(rota.Changed.Count, "changed", "changed"));
        }

        if (rota.Removed.Count > 0)
        {
            parts.Add(Count(rota.Removed.Count, "removed", "removed"));
        }

        // With one shift involved, name it; otherwise the counts say more than a list would.
        List<ShiftEmailLine> all = [.. rota.Added, .. rota.Changed, .. rota.Removed];
        string kind = rota.Added.Count == 1 ? "new" : rota.Changed.Count == 1 ? "changed" : "removed";
        string body = all.Count == 1 ? $"{ShiftClaimNotices.Line(all[0])} ({kind})" : string.Join(", ", parts);

        return new PushContent(title, body, "/rota", $"rota-{rota.LocationId}", PushMessageUrgency.Normal, TimeSpan.FromDays(3));
    }

    private static PushContent Reminder(ShiftReminderPayload reminder, DateOnly today)
    {
        string day = reminder.Shift.Date == today ? "today"
            : reminder.Shift.Date == today.AddDays(1) ? "tomorrow"
            : reminder.Shift.Date.ToString("ddd d MMM", RotaFormat.Uk);

        string body = reminder.Shift.PositionName is { Length: > 0 } position
            ? $"{reminder.Shift.TimeRange} at {reminder.LocationName} · {position}"
            : $"{reminder.Shift.TimeRange} at {reminder.LocationName}";

        // A reminder delivered after the shift has started is noise, so it expires within the day.
        return new PushContent($"You're working {day}", body, "/rota", $"shift-reminder-{reminder.Shift.Date:yyyyMMdd}",
            PushMessageUrgency.High, TimeSpan.FromHours(12));
    }

    private static PushContent Decided(TimeOffDecidedPayload decided)
    {
        string title = decided.Outcome switch
        {
            TimeOffEmailOutcome.Approved => "Your time off is approved",
            TimeOffEmailOutcome.Rejected => "Your time off wasn't approved",
            TimeOffEmailOutcome.Withdrawn => "Your approved time off has been withdrawn",
            TimeOffEmailOutcome.CutShort => "Your time off has been cut short",
            _ => "Time off has been recorded for you"
        };

        string body = $"{decided.TypeName} · {TimeOffFormat.DateRange(decided.Start, decided.End)}";

        if (!string.IsNullOrWhiteSpace(decided.Reason))
        {
            body += $" · \"{decided.Reason}\"";
        }

        return new PushContent(title, body, "/rota/time-off", $"time-off-{decided.Start:yyyyMMdd}", PushMessageUrgency.Normal, TimeSpan.FromDays(3));
    }

    // Open shifts and pick-ups are time-sensitive: someone has called off and the gap needs filling.
    private static PushContent FromNotice(NotificationPayload payload)
    {
        Notice notice = ShiftClaimNotices.For(payload);
        bool urgent = payload is OpenShiftPayload or ShiftClaimPendingPayload or ShiftClaimDecidedPayload;

        return new PushContent(notice.Title, string.Join(" · ", notice.Lines), notice.Url, notice.Tag,
            urgent ? PushMessageUrgency.High : PushMessageUrgency.Normal, TimeSpan.FromDays(2));
    }

    // "Tom Hughes" / "Tom Hughes in Weekend crew", with the message (or just that there is one,
    // for anyone who has turned message text off - Preview is empty then). Same tag per
    // conversation, so a burst of messages shows as one notification that keeps updating.
    private static PushContent ChatMessage(ChatMessagePayload chat)
    {
        string title = chat.IsGroup ? $"{chat.AuthorName} in {chat.ConversationName}" : chat.AuthorName;
        string body = string.IsNullOrWhiteSpace(chat.Preview) ? "New message" : chat.Preview;

        return new PushContent(title, body, $"/chat/{chat.ConversationId}", $"chat-{chat.ConversationId:N}", PushMessageUrgency.High, TimeSpan.FromDays(1));
    }

    private static PushContent FromChatNotice(NotificationPayload payload)
    {
        Notice notice = ChatNotices.For(payload);

        return new PushContent(notice.Title, string.Join(" · ", notice.Lines), notice.Url, notice.Tag, PushMessageUrgency.Normal, TimeSpan.FromDays(2));
    }

    private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
