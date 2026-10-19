using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Notifications;

// One shift as it appears in an email: "Mon 5 Oct · 09:00–17:00 · Supervisor". Formatted when
// the notification is queued, so delivery needs no rota lookups.
public record ShiftEmailLine(DateOnly Date, string TimeRange, string? PositionName);

// What a notification says, as plain data. Nothing here holds a service or a DbContext: jobs are
// delivered later on a background worker, in their own scope. Each payload names the location it
// belongs to, which is what company branding (logo, colour) is resolved from.
public abstract record NotificationPayload(int LocationId);

public record RotaChangedPayload(
    int LocationId,
    string LocationName,
    List<ShiftEmailLine> Added,
    List<ShiftEmailLine> Changed,
    List<ShiftEmailLine> Removed) : NotificationPayload(LocationId);

public record ShiftReminderPayload(int LocationId, string LocationName, ShiftEmailLine Shift) : NotificationPayload(LocationId);

public record TimeOffRequestedPayload(
    int LocationId,
    string RequesterName,
    string TypeName,
    DateOnly Start,
    DateOnly End,
    string Amount,
    string? Notes) : NotificationPayload(LocationId);

public record TimeOffDecidedPayload(
    int LocationId,
    string TypeName,
    DateOnly Start,
    DateOnly End,
    TimeOffEmailOutcome Outcome,
    string? Reason,
    string? DecidedByName) : NotificationPayload(LocationId);

// A shift nobody is working yet, sent to everyone who could pick it up.
public record OpenShiftPayload(int LocationId, string LocationName, ShiftEmailLine Shift, bool NeedsApproval) : NotificationPayload(LocationId);

// Someone is looking for a swap, sent to colleagues who could take their shift.
public record SwapRequestedPayload(int LocationId, string LocationName, string RequesterName, ShiftEmailLine Shift, string? Note) : NotificationPayload(LocationId);

// A colleague answered your swap request with one of their shifts.
public record SwapOfferedPayload(int LocationId, string LocationName, string OffererName, ShiftEmailLine YourShift, ShiftEmailLine TheirShift) : NotificationPayload(LocationId);

// For managers: a pick-up, call-off or swap waiting for their decision.
public record ShiftClaimPendingPayload(
    int LocationId,
    string LocationName,
    ShiftClaimKind Kind,
    string PersonName,
    ShiftEmailLine Shift,
    string? OtherPersonName,
    ShiftEmailLine? OtherShift,
    string? Note) : NotificationPayload(LocationId);

// For the person who asked: how their pick-up, call-off, swap or swap offer turned out. For a
// swap, Shift is the one they now work (or would have) and OtherShift the one they gave away.
public record ShiftClaimDecidedPayload(
    int LocationId,
    string LocationName,
    ShiftClaimKind Kind,
    bool Approved,
    ShiftEmailLine Shift,
    ShiftEmailLine? OtherShift,
    string? Reason) : NotificationPayload(LocationId);

// A new chat message, for members who aren't looking at the conversation. Push only; Preview is
// dropped at delivery for anyone who has turned message text off.
public record ChatMessagePayload(Guid ConversationId, bool IsGroup, string ConversationName, string AuthorName, string Preview)
    : NotificationPayload(0);

// One conversation in the daily unread-messages email: its name and how many messages are waiting.
public record ChatDigestLine(string ConversationName, int Count);

// The daily email about messages left unread for a day. Names and counts only, never message text.
public record ChatDigestPayload(List<ChatDigestLine> Lines) : NotificationPayload(0);

// A post pinned in a channel the person is in: news from their managers. Includes the text, as
// pinned posts are announcements rather than conversation.
public record PinnedPostPayload(Guid ConversationId, string ChannelName, string AuthorName, string Preview) : NotificationPayload(0);

// For managers: a chat message at their location was reported.
public record ChatReportedPayload(int LocationId) : NotificationPayload(LocationId);

// For the person who reported a message: a manager has dealt with it (not what they did).
public record ChatReportReviewedPayload(int LocationId) : NotificationPayload(LocationId);

// A manager posted an announcement and chose to send it out to everyone at its location. Carries
// the full text: an announcement is meant to be read, and the email is often where it is read.
public record AnnouncementPayload(int LocationId, string LocationName, Guid AnnouncementId, string Title, string Body, string? AuthorName)
    : NotificationPayload(LocationId);

// For managers and Admins: a kiosk has been down long enough to count as offline. LocationId is 0
// for a kiosk with no location set. DownSince is already in venue time ("14:02", or with the day
// when it wasn't today), Url differs for Managers, who can't open the Clients page.
public record KioskOfflinePayload(
    int LocationId,
    Guid ClientId,
    string ClientName,
    string? LocationName,
    string DownSince,
    bool ShutDownCleanly,
    bool OfflineCopyReady,
    string Url) : NotificationPayload(LocationId);

// Sent only after a KioskOfflinePayload went out for the same outage.
public record KioskBackOnlinePayload(int LocationId, Guid ClientId, string ClientName, string? LocationName, int DownForMinutes, string Url)
    : NotificationPayload(LocationId);

// A kiosk has been degraded for a while; Reasons are the health page's own wording.
public record KioskNeedsAttentionPayload(int LocationId, Guid ClientId, string ClientName, string? LocationName, List<string> Reasons, string Url)
    : NotificationPayload(LocationId);

public enum PolicyReminderKind
{
    // Newly asked to read it: a first version, or they've just joined the audience.
    New = 0,

    // They'd acknowledged it before; a new version or the re-read schedule needs them again.
    ReadAgain = 1,

    // Past the date they were asked to acknowledge it by.
    Overdue = 2
}

// Someone has a policy to read and acknowledge. DueDate is already in venue time ("30 Oct").
// LocationId is the policy's location, or for a company-wide policy one of the person's own.
public record PolicyReminderPayload(int LocationId, Guid PolicyId, string PolicyName, int VersionNumber, string DueDate, PolicyReminderKind Kind)
    : NotificationPayload(LocationId);

public record NotificationJob(string UserId, NotificationTopic Topic, NotificationPayload Payload);
