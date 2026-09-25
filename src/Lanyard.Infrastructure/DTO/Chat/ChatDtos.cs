using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Chat;

// One row of the inbox. Title is the other person's name for a direct conversation.
public record ChatInboxItem(
    Guid ConversationId,
    ChatConversationKind Kind,
    string Title,
    string? Preview,
    DateTime LastMessageUtc,
    int UnreadCount,
    bool IsMuted);

// Someone the viewer could start a conversation with.
public record ChatPerson(string UserId, string Name, bool BlockedByMe);

public record ChatMemberView(string UserId, string Name, bool IsGroupAdmin);

public record ChatReplyPreview(Guid MessageId, string AuthorName, string Text, bool IsDeleted);

public record ChatMessageView(ChatMessage Message, string AuthorName, bool IsMine, ChatReplyPreview? ReplyTo)
{
    // For an open-shift or swap card: what it links to, as it stands now.
    public ChatCardView? Card { get; init; }
}

// A card's live state. IsOpen: still something people can act on (the button shows).
public record ChatCardView(string Title, string Detail, string Status, bool IsOpen, string ActionLabel, string ActionUrl);

// A pinned channel post for the top of the chat page.
public record ChatPinnedPost(Guid ConversationId, string ChannelName, ChatMessageView Message);

// An open conversation as its member sees it. CannotPostReason is null when they can post.
public record ChatThread(
    ChatConversation Conversation,
    string Title,
    List<ChatMemberView> Members,
    List<ChatMessageView> Messages,
    bool HasOlder,
    bool IsMuted,
    bool IsGroupAdmin,
    string? OtherUserId,
    bool BlockedByMe,
    string? CannotPostReason)
{
    // Channels: the viewer can pin, unpin and remove others' messages here.
    public bool CanModerate { get; init; }

    // Channels: pinned posts, newest first, for the strip at the top.
    public List<ChatMessageView> Pinned { get; init; } = [];
}

// A report as a manager reviews it: the snapshot, never the conversation around it.
public record ChatReportView(ChatReport Report, string ReporterName, string ReportedName, bool WasDirectMessage, bool MessageStillVisible)
{
    // "direct message" / "group message" / "channel message", for the card.
    public string WhereLabel { get; init; } = "group message";
}

// One channel on the managers' Chat Channels page.
public record ChatChannelAdminView(ChatConversation Channel, int MemberCount, int PinnedCount);

// A message a manager removed from a channel: who wrote it, where and when - not what it said.
public record ChatRemovalView(string ChannelName, string AuthorName, string RemovedByName, DateTime SentUtc, DateTime RemovedUtc);

public record ChatSuspensionView(ChatSuspension Suspension, string Name, string ImposedByName);
