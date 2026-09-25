using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A conversation: two people (Direct) or a named group. Everyone in it belongs to one company.
    // Direct conversations are private to their two members by design - no screen or service method
    // shows one to anybody else, Admins included. The only exception is a single message a member
    // chose to report, and then only a snapshot of that message (ChatReport).
    public class ChatConversation
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public ChatConversationKind Kind { get; set; }

        // Channels (next chat work) belong to one location.
        public int? LocationId { get; set; }

        // Groups and channels only; a direct conversation is named after the other person.
        public string? Name { get; set; }

        // Direct only: the two user ids, sorted and joined, so the same pair can't get two
        // conversations (partial unique index).
        public string? DirectKey { get; set; }

        public string? CreatedByUserId { get; set; }
        public DateTime CreateDate { get; set; }

        // When the newest message was posted; orders the inbox.
        public DateTime LastMessageUtc { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class ChatMember
    {
        public Guid Id { get; set; }

        public Guid ConversationId { get; set; }
        public ChatConversation? Conversation { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public DateTime JoinedUtc { get; set; }

        // Left a group: no longer sees it or gets its messages.
        public DateTime? LeftUtc { get; set; }

        // Everything posted up to here has been seen; later messages from others are unread.
        public DateTime? LastReadUtc { get; set; }

        // The newest unread message the daily unread-messages email has already mentioned, so
        // it isn't mentioned twice.
        public DateTime? LastDigestEmailUtc { get; set; }

        // No push notifications from this conversation (unread counts still show).
        public bool IsMuted { get; set; }

        public bool IsGroupAdmin { get; set; }
    }

    public class ChatMessage
    {
        public Guid Id { get; set; }

        public Guid ConversationId { get; set; }
        public ChatConversation? Conversation { get; set; }

        public required string AuthorUserId { get; set; }
        public UserProfile? Author { get; set; }

        // Rich text from the composer, sanitised on save (ChatHtml). Cleared when the message is
        // deleted, so a deleted message is gone rather than hidden.
        public required string BodyHtml { get; set; }

        // The same message as plain text, for previews and notifications.
        public required string BodyText { get; set; }

        public DateTime CreateUtc { get; set; }
        public DateTime? EditedUtc { get; set; }

        public DateTime? DeletedUtc { get; set; }

        // Null when the author deleted it; a manager's id when removed after a report.
        public string? DeletedByUserId { get; set; }

        public Guid? ReplyToMessageId { get; set; }
        public ChatMessage? ReplyTo { get; set; }

        public bool IsDeleted => DeletedUtc is not null;
    }

    // BlockerUserId won't get direct messages from BlockedUserId, and neither can start or continue
    // a direct conversation with the other. Groups are unaffected.
    public class ChatBlock
    {
        public Guid Id { get; set; }

        public required string BlockerUserId { get; set; }
        public UserProfile? Blocker { get; set; }

        public required string BlockedUserId { get; set; }
        public UserProfile? Blocked { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    // One reported message, for the reporter's location's managers to review. Holds a snapshot of
    // the message as it was, so deleting it afterwards doesn't destroy the evidence - and is all a
    // manager ever sees of the conversation.
    public class ChatReport
    {
        public Guid Id { get; set; }

        public Guid MessageId { get; set; }
        public ChatMessage? Message { get; set; }

        public required string ReporterUserId { get; set; }
        public UserProfile? Reporter { get; set; }

        public required string ReportedUserId { get; set; }
        public UserProfile? Reported { get; set; }

        public ChatReportReason Reason { get; set; }
        public string? Details { get; set; }

        public required string MessageHtmlSnapshot { get; set; }
        public DateTime MessageSentUtc { get; set; }

        // The reporter's location; its managers review.
        public int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateTime CreatedUtc { get; set; }

        public ChatReportStatus Status { get; set; } = ChatReportStatus.Open;
        public string? ReviewedByUserId { get; set; }
        public DateTime? ReviewedUtc { get; set; }

        // What the manager did, in their words; not shown to the reporter or the reported person.
        public string? Outcome { get; set; }
    }

    // A person barred from posting in chat (they can still read) until UntilUtc, or until lifted
    // when UntilUtc is null.
    public class ChatSuspension
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public int CompanyId { get; set; }

        public DateTime CreatedUtc { get; set; }
        public DateTime? UntilUtc { get; set; }
        public required string Reason { get; set; }
        public required string ImposedByUserId { get; set; }

        public DateTime? LiftedUtc { get; set; }
        public string? LiftedByUserId { get; set; }

        public bool IsActiveAt(DateTime nowUtc) => LiftedUtc is null && (UntilUtc is null || UntilUtc > nowUtc);
    }
}
