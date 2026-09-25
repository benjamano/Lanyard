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

public record ChatMessageView(ChatMessage Message, string AuthorName, bool IsMine, ChatReplyPreview? ReplyTo);

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
    string? CannotPostReason);

// A report as a manager reviews it: the snapshot, never the conversation around it.
public record ChatReportView(ChatReport Report, string ReporterName, string ReportedName, bool WasDirectMessage, bool MessageStillVisible);

public record ChatSuspensionView(ChatSuspension Suspension, string Name, string ImposedByName);
