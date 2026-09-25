using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Chat;

// Direct and group conversations. Every method acts as the given person and only ever shows them
// conversations they belong to: there is no manager or admin view of anyone else's messages.
// companyId is the company the person is signed in under; conversations never cross companies.
public interface IChatService
{
    Task<Result<List<ChatInboxItem>>> GetInboxAsync(string userId);

    // People in the company the person could message (themselves left out).
    Task<Result<List<ChatPerson>>> GetPeopleAsync(string userId, int companyId);

    // The one direct conversation between the two, created on first use.
    Task<Result<ChatConversation>> GetOrCreateDirectAsync(string userId, int companyId, string otherUserId);

    Task<Result<ChatConversation>> CreateGroupAsync(string userId, int companyId, string name, IReadOnlyCollection<string> memberUserIds);

    Task<Result<bool>> AddMembersAsync(string userId, Guid conversationId, IReadOnlyCollection<string> memberUserIds);

    Task<Result<bool>> RenameGroupAsync(string userId, Guid conversationId, string name);

    Task<Result<bool>> LeaveGroupAsync(string userId, Guid conversationId);

    // The newest messages (or those before `before`, for scrolling back), oldest first.
    Task<Result<ChatThread>> GetThreadAsync(string userId, Guid conversationId, DateTime? before = null, int take = 50);

    Task<Result<ChatMessage>> SendAsync(string userId, Guid conversationId, string html, Guid? replyToMessageId = null);

    Task<Result<bool>> EditAsync(string userId, Guid messageId, string html);

    Task<Result<bool>> DeleteOwnAsync(string userId, Guid messageId);

    Task<Result<bool>> MarkReadAsync(string userId, Guid conversationId);

    Task<Result<int>> GetUnreadTotalAsync(string userId);

    Task<Result<bool>> SetMutedAsync(string userId, Guid conversationId, bool muted);

    // Stops direct messages both ways between the two; groups carry on as before.
    Task<Result<bool>> BlockAsync(string userId, string otherUserId);

    Task<Result<bool>> UnblockAsync(string userId, string otherUserId);
}
