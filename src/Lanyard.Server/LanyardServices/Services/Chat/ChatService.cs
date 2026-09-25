using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Chat;

public class ChatService(
    IDbContextFactory<ApplicationDbContext> factory,
    INotificationDispatcher notifications,
    IChatEventBus eventBus,
    IChatPresence presence,
    TimeProvider timeProvider,
    ILogger<ChatService> logger) : IChatService
{
    public const int MaxGroupNameLength = 80;
    public const int MaxGroupSize = 100;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly IChatEventBus _eventBus = eventBus;
    private readonly IChatPresence _presence = presence;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChatService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<Result<List<ChatInboxItem>>> GetInboxAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ChatMember> mine = await ctx.ChatMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Conversation)
                .Where(x => x.UserId == userId && x.LeftUtc == null && x.Conversation!.IsActive)
                .ToListAsync();

            List<Guid> conversationIds = mine.Select(x => x.ConversationId).ToList();

            List<ChatMessage> latest = await ctx.ChatMessages
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => conversationIds.Contains(x.ConversationId))
                .GroupBy(x => x.ConversationId)
                .Select(g => g.OrderByDescending(x => x.CreateUtc).First())
                .ToListAsync();

            Dictionary<Guid, int> unread = await UnreadByConversationAsync(ctx, userId, conversationIds);
            Dictionary<Guid, string> titles = await TitlesAsync(ctx, userId, mine.Select(x => x.Conversation!).ToList());

            List<ChatInboxItem> items = mine
                .Select(member =>
                {
                    ChatMessage? last = latest.FirstOrDefault(x => x.ConversationId == member.ConversationId);
                    string? preview = last is null ? null
                        : last.IsDeleted ? "Message deleted"
                        : ChatHtml.Preview(last.BodyText, 80);

                    return new ChatInboxItem(
                        member.ConversationId,
                        member.Conversation!.Kind,
                        titles.GetValueOrDefault(member.ConversationId, "Conversation"),
                        preview,
                        member.Conversation.LastMessageUtc,
                        unread.GetValueOrDefault(member.ConversationId),
                        member.IsMuted);
                })
                .OrderByDescending(x => x.LastMessageUtc)
                .ToList();

            return Result<List<ChatInboxItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the chat inbox for {UserId}", userId);
            return Result<List<ChatInboxItem>>.Fail($"Failed to load your conversations: {ex.Message}");
        }
    }

    public async Task<Result<List<ChatPerson>>> GetPeopleAsync(string userId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<string> ids = await ChatRules.CompanyMemberIds(ctx, companyId).Where(x => x != userId).ToListAsync();

            List<UserProfile> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync();

            HashSet<string> blocked = (await ctx.ChatBlocks
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.BlockerUserId == userId)
                .Select(x => x.BlockedUserId)
                .ToListAsync()).ToHashSet();

            List<ChatPerson> people = users
                .Select(x => new ChatPerson(x.Id, RotaNames.For(x), blocked.Contains(x.Id)))
                .OrderBy(x => x.Name)
                .ToList();

            return Result<List<ChatPerson>>.Ok(people);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat people for {UserId}", userId);
            return Result<List<ChatPerson>>.Fail($"Failed to load people: {ex.Message}");
        }
    }

    public async Task<Result<ChatConversation>> GetOrCreateDirectAsync(string userId, int companyId, string otherUserId)
    {
        try
        {
            if (otherUserId == userId)
            {
                return Result<ChatConversation>.Fail("You can't start a conversation with yourself.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ChatRules.InCompanyAsync(ctx, companyId, [userId, otherUserId]))
            {
                return Result<ChatConversation>.Fail("You can only message people at your company.");
            }

            if (await ChatRules.BlockedEitherWayAsync(ctx, userId, otherUserId))
            {
                return Result<ChatConversation>.Fail("You can't message this person.");
            }

            string key = ChatRules.DirectKey(userId, otherUserId);

            ChatConversation? existing = await ctx.ChatConversations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.DirectKey == key);

            if (existing is not null)
            {
                return Result<ChatConversation>.Ok(existing);
            }

            DateTime now = Now;

            ChatConversation conversation = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Kind = ChatConversationKind.Direct,
                DirectKey = key,
                CreatedByUserId = userId,
                CreateDate = now,
                LastMessageUtc = now
            };

            ctx.ChatConversations.Add(conversation);
            ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversation.Id, UserId = userId, JoinedUtc = now, LastReadUtc = now });
            ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversation.Id, UserId = otherUserId, JoinedUtc = now, LastReadUtc = now });

            try
            {
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // The other person started it at the same moment; use theirs.
                await using ApplicationDbContext retry = await _factory.CreateDbContextAsync();
                ChatConversation? theirs = await retry.ChatConversations.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.DirectKey == key);

                return theirs is not null ? Result<ChatConversation>.Ok(theirs) : Result<ChatConversation>.Fail("Couldn't start the conversation. Try again.");
            }

            return Result<ChatConversation>.Ok(conversation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a direct conversation for {UserId}", userId);
            return Result<ChatConversation>.Fail($"Couldn't start the conversation: {ex.Message}");
        }
    }

    public async Task<Result<ChatConversation>> CreateGroupAsync(string userId, int companyId, string name, IReadOnlyCollection<string> memberUserIds)
    {
        try
        {
            string? nameError = ValidateGroupName(name);

            if (nameError is not null)
            {
                return Result<ChatConversation>.Fail(nameError);
            }

            List<string> others = memberUserIds.Where(x => !string.IsNullOrEmpty(x) && x != userId).Distinct().ToList();

            if (others.Count == 0)
            {
                return Result<ChatConversation>.Fail("Choose at least one person for the group.");
            }

            if (others.Count + 1 > MaxGroupSize)
            {
                return Result<ChatConversation>.Fail($"A group can have up to {MaxGroupSize} people.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ChatRules.InCompanyAsync(ctx, companyId, [userId, .. others]))
            {
                return Result<ChatConversation>.Fail("Everyone in a group must be at your company.");
            }

            if (await ChatRules.SuspendedUntilAsync(ctx, userId, companyId, Now) is { } suspended)
            {
                return Result<ChatConversation>.Fail(suspended);
            }

            DateTime now = Now;

            ChatConversation conversation = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Kind = ChatConversationKind.Group,
                Name = name.Trim(),
                CreatedByUserId = userId,
                CreateDate = now,
                LastMessageUtc = now
            };

            ctx.ChatConversations.Add(conversation);
            ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversation.Id, UserId = userId, JoinedUtc = now, LastReadUtc = now, IsGroupAdmin = true });

            foreach (string other in others)
            {
                ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversation.Id, UserId = other, JoinedUtc = now });
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} created chat group {ConversationId} with {Count} people", userId, conversation.Id, others.Count + 1);
            _eventBus.Publish(new ChatEvent(conversation.Id, [userId, .. others], ChatEventKind.ConversationChanged));

            return Result<ChatConversation>.Ok(conversation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create a chat group for {UserId}", userId);
            return Result<ChatConversation>.Fail($"Couldn't create the group: {ex.Message}");
        }
    }

    public async Task<Result<bool>> AddMembersAsync(string userId, Guid conversationId, IReadOnlyCollection<string> memberUserIds)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, conversationId, tracked: false);

            if (conversation is null || me is null || conversation.Kind != ChatConversationKind.Group)
            {
                return Result<bool>.Fail("That group isn't available.");
            }

            // Changing a group is posting in it as far as everyone else can see, so a suspension
            // stops it too. (Any member may add people or rename: groups are informal, and there's
            // no owner to ask.)
            if (await ChatRules.SuspendedUntilAsync(ctx, userId, conversation.CompanyId, Now) is { } suspended)
            {
                return Result<bool>.Fail(suspended);
            }

            List<string> adding = memberUserIds.Where(x => !string.IsNullOrEmpty(x) && x != userId).Distinct().ToList();

            if (!await ChatRules.InCompanyAsync(ctx, conversation.CompanyId, adding))
            {
                return Result<bool>.Fail("Everyone in a group must be at your company.");
            }

            List<ChatMember> existing = await ctx.ChatMembers.Where(x => x.ConversationId == conversationId).ToListAsync();

            if (existing.Count(x => x.LeftUtc == null) + adding.Count(a => existing.All(e => e.UserId != a || e.LeftUtc != null)) > MaxGroupSize)
            {
                return Result<bool>.Fail($"A group can have up to {MaxGroupSize} people.");
            }

            DateTime now = Now;

            foreach (string id in adding)
            {
                ChatMember? member = existing.FirstOrDefault(x => x.UserId == id);

                if (member is null)
                {
                    ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversationId, UserId = id, JoinedUtc = now });
                }
                else if (member.LeftUtc is not null)
                {
                    // A fresh start: what was said while they were out is neither theirs to read
                    // nor unread for them.
                    member.LeftUtc = null;
                    member.JoinedUtc = now;
                    member.LastReadUtc = now;
                    member.LastDigestEmailUtc = null;
                    member.IsMuted = false;
                }
            }

            await ctx.SaveChangesAsync();
            await PublishAsync(ctx, conversationId, ChatEventKind.ConversationChanged);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add people to chat group {ConversationId}", conversationId);
            return Result<bool>.Fail($"Couldn't add people: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RenameGroupAsync(string userId, Guid conversationId, string name)
    {
        try
        {
            string? nameError = ValidateGroupName(name);

            if (nameError is not null)
            {
                return Result<bool>.Fail(nameError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, conversationId, tracked: true);

            if (conversation is null || me is null || conversation.Kind != ChatConversationKind.Group)
            {
                return Result<bool>.Fail("That group isn't available.");
            }

            if (await ChatRules.SuspendedUntilAsync(ctx, userId, conversation.CompanyId, Now) is { } suspended)
            {
                return Result<bool>.Fail(suspended);
            }

            conversation.Name = name.Trim();
            await ctx.SaveChangesAsync();
            await PublishAsync(ctx, conversationId, ChatEventKind.ConversationChanged);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename chat group {ConversationId}", conversationId);
            return Result<bool>.Fail($"Couldn't rename the group: {ex.Message}");
        }
    }

    public async Task<Result<bool>> LeaveGroupAsync(string userId, Guid conversationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, conversationId, tracked: true);

            if (conversation is null || me is null || conversation.Kind != ChatConversationKind.Group)
            {
                return Result<bool>.Fail("That group isn't available.");
            }

            me.LeftUtc = Now;
            me.IsGroupAdmin = false;
            await ctx.SaveChangesAsync();

            _eventBus.Publish(new ChatEvent(conversationId, [userId], ChatEventKind.ConversationChanged));
            await PublishAsync(ctx, conversationId, ChatEventKind.ConversationChanged);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to leave chat group {ConversationId}", conversationId);
            return Result<bool>.Fail($"Couldn't leave the group: {ex.Message}");
        }
    }

    public async Task<Result<ChatThread>> GetThreadAsync(string userId, Guid conversationId, DateTime? before = null, int take = 50)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, conversationId, tracked: false);

            // Not a member (or left): exactly the same answer as a conversation that doesn't exist,
            // so the id of someone else's conversation reveals nothing.
            if (conversation is null || me is null)
            {
                return Result<ChatThread>.Fail("That conversation isn't available.");
            }

            take = Math.Clamp(take, 1, 200);

            // A member sees what was said from when they (last) joined - someone added to a group,
            // or added back after leaving, doesn't get the conversation from before.
            DateTime joined = me.JoinedUtc;

            IQueryable<ChatMessage> query = ctx.ChatMessages
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Author)
                .Include(x => x.ReplyTo).ThenInclude(x => x!.Author)
                .Where(x => x.ConversationId == conversationId && x.CreateUtc >= joined);

            if (before is DateTime cutoff)
            {
                query = query.Where(x => x.CreateUtc < cutoff);
            }

            List<ChatMessage> newestFirst = await query
                .OrderByDescending(x => x.CreateUtc)
                .Take(take + 1)
                .ToListAsync();

            bool hasOlder = newestFirst.Count > take;

            List<ChatMessageView> messages = newestFirst
                .Take(take)
                .Reverse()
                .Select(x => new ChatMessageView(
                    x,
                    RotaNames.For(x.Author),
                    x.AuthorUserId == userId,
                    x.ReplyTo is null ? null
                        : x.ReplyTo.CreateUtc < joined ? new ChatReplyPreview(x.ReplyTo.Id, "Earlier message", "From before you joined", true)
                        : new ChatReplyPreview(x.ReplyTo.Id, RotaNames.For(x.ReplyTo.Author), ChatHtml.Preview(x.ReplyTo.BodyText, 100), x.ReplyTo.IsDeleted)))
                .ToList();

            List<ChatMember> members = await ctx.ChatMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Where(x => x.ConversationId == conversationId && x.LeftUtc == null)
                .ToListAsync();

            string? otherUserId = conversation.Kind == ChatConversationKind.Direct ? members.FirstOrDefault(x => x.UserId != userId)?.UserId : null;
            bool blockedByMe = otherUserId is not null && await ctx.ChatBlocks.AsNoTracking().AnyAsync(x => x.BlockerUserId == userId && x.BlockedUserId == otherUserId);

            string title = conversation.Kind == ChatConversationKind.Direct
                ? RotaNames.For(members.FirstOrDefault(x => x.UserId != userId)?.User)
                : conversation.Name ?? "Group";

            string? cannotPost = await CannotPostReasonAsync(ctx, userId, conversation, otherUserId);

            return Result<ChatThread>.Ok(new ChatThread(
                conversation,
                title,
                members.Select(x => new ChatMemberView(x.UserId, RotaNames.For(x.User), x.IsGroupAdmin)).OrderBy(x => x.Name).ToList(),
                messages,
                hasOlder,
                me.IsMuted,
                me.IsGroupAdmin,
                otherUserId,
                blockedByMe,
                cannotPost));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat conversation {ConversationId} for {UserId}", conversationId, userId);
            return Result<ChatThread>.Fail($"Failed to load the conversation: {ex.Message}");
        }
    }

    public async Task<Result<ChatMessage>> SendAsync(string userId, Guid conversationId, string html, Guid? replyToMessageId = null)
    {
        try
        {
            ChatHtml.Cleaned? cleaned = ChatHtml.Clean(html);

            if (cleaned is null)
            {
                return Result<ChatMessage>.Fail("Type a message first.");
            }

            if (cleaned.Html.Length > ChatHtml.MaxHtmlLength || cleaned.Text.Length > ChatHtml.MaxTextLength)
            {
                return Result<ChatMessage>.Fail("That message is too long. Split it into a few shorter ones.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, conversationId, tracked: true);

            if (conversation is null || me is null)
            {
                return Result<ChatMessage>.Fail("That conversation isn't available.");
            }

            List<ChatMember> members = await ctx.ChatMembers
                .Include(x => x.User)
                .Where(x => x.ConversationId == conversationId && x.LeftUtc == null)
                .ToListAsync();

            string? otherUserId = conversation.Kind == ChatConversationKind.Direct ? members.FirstOrDefault(x => x.UserId != userId)?.UserId : null;
            string? cannotPost = await CannotPostReasonAsync(ctx, userId, conversation, otherUserId);

            if (cannotPost is not null)
            {
                return Result<ChatMessage>.Fail(cannotPost);
            }

            if (replyToMessageId is Guid replyId && !await ctx.ChatMessages.AnyAsync(x => x.Id == replyId && x.ConversationId == conversationId))
            {
                replyToMessageId = null;
            }

            DateTime now = Now;

            ChatMessage message = new()
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                AuthorUserId = userId,
                BodyHtml = cleaned.Html,
                BodyText = cleaned.Text,
                CreateUtc = now,
                ReplyToMessageId = replyToMessageId
            };

            ctx.ChatMessages.Add(message);
            conversation.LastMessageUtc = now;
            members.First(x => x.UserId == userId).LastReadUtc = now;

            await ctx.SaveChangesAsync();

            _eventBus.Publish(new ChatEvent(conversationId, members.Select(x => x.UserId).ToList(), ChatEventKind.MessagePosted));

            try
            {
                // Everyone else who isn't looking at the conversation right now and hasn't muted it.
                List<string> toPush = members
                    .Where(x => x.UserId != userId && !x.IsMuted && !_presence.IsViewing(x.UserId, conversationId))
                    .Select(x => x.UserId)
                    .ToList();

                string author = RotaNames.For(members.First(x => x.UserId == userId).User);
                bool isGroup = conversation.Kind != ChatConversationKind.Direct;

                _notifications.Enqueue(toPush, isGroup ? NotificationTopic.GroupMessage : NotificationTopic.DirectMessage,
                    new ChatMessagePayload(conversationId, isGroup, isGroup ? conversation.Name ?? "Group" : author, author, ChatHtml.Preview(cleaned.Text)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sent chat message {MessageId} but couldn't queue its notifications", message.Id);
            }

            return Result<ChatMessage>.Ok(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send a chat message in {ConversationId} for {UserId}", conversationId, userId);
            return Result<ChatMessage>.Fail($"Couldn't send the message: {ex.Message}");
        }
    }

    public async Task<Result<bool>> EditAsync(string userId, Guid messageId, string html)
    {
        try
        {
            ChatHtml.Cleaned? cleaned = ChatHtml.Clean(html);

            if (cleaned is null)
            {
                return Result<bool>.Fail("A message can't be empty. Delete it instead.");
            }

            if (cleaned.Html.Length > ChatHtml.MaxHtmlLength || cleaned.Text.Length > ChatHtml.MaxTextLength)
            {
                return Result<bool>.Fail("That message is too long.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatMessage? message = await ctx.ChatMessages.FirstOrDefaultAsync(x => x.Id == messageId);

            if (message is null || message.AuthorUserId != userId || message.IsDeleted)
            {
                return Result<bool>.Fail("You can only edit your own messages.");
            }

            (ChatConversation? conversation, ChatMember? me) = await MembershipAsync(ctx, userId, message.ConversationId, tracked: false);

            if (conversation is null || me is null)
            {
                return Result<bool>.Fail("That conversation isn't available.");
            }

            // Editing is posting: the same suspension and block rules as sending.
            string? otherUserId = conversation.Kind == ChatConversationKind.Direct
                ? await ctx.ChatMembers.AsNoTracking().Where(x => x.ConversationId == conversation.Id && x.UserId != userId).Select(x => x.UserId).FirstOrDefaultAsync()
                : null;

            if (await CannotPostReasonAsync(ctx, userId, conversation, otherUserId) is { } cannotPost)
            {
                return Result<bool>.Fail(cannotPost);
            }

            message.BodyHtml = cleaned.Html;
            message.BodyText = cleaned.Text;
            message.EditedUtc = Now;

            await ctx.SaveChangesAsync();
            await PublishAsync(ctx, message.ConversationId, ChatEventKind.MessageChanged);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit chat message {MessageId}", messageId);
            return Result<bool>.Fail($"Couldn't edit the message: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteOwnAsync(string userId, Guid messageId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatMessage? message = await ctx.ChatMessages.FirstOrDefaultAsync(x => x.Id == messageId);

            if (message is null || message.AuthorUserId != userId)
            {
                return Result<bool>.Fail("You can only delete your own messages.");
            }

            if (message.IsDeleted)
            {
                return Result<bool>.Ok(true);
            }

            ChatRules.Erase(message, null, Now);

            await ctx.SaveChangesAsync();
            await PublishAsync(ctx, message.ConversationId, ChatEventKind.MessageChanged);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat message {MessageId}", messageId);
            return Result<bool>.Fail($"Couldn't delete the message: {ex.Message}");
        }
    }

    public async Task<Result<bool>> MarkReadAsync(string userId, Guid conversationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatMember? me = await ctx.ChatMembers.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == userId && x.LeftUtc == null);

            if (me is null)
            {
                return Result<bool>.Fail("That conversation isn't available.");
            }

            me.LastReadUtc = Now;
            await ctx.SaveChangesAsync();

            _eventBus.Publish(new ChatEvent(conversationId, [userId], ChatEventKind.Read));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark chat {ConversationId} read for {UserId}", conversationId, userId);
            return Result<bool>.Fail($"Couldn't mark the conversation read: {ex.Message}");
        }
    }

    public async Task<Result<int>> GetUnreadTotalAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Guid> conversationIds = await ctx.ChatMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.LeftUtc == null && x.Conversation!.IsActive)
                .Select(x => x.ConversationId)
                .ToListAsync();

            Dictionary<Guid, int> unread = await UnreadByConversationAsync(ctx, userId, conversationIds);

            return Result<int>.Ok(unread.Values.Sum());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count unread chat messages for {UserId}", userId);
            return Result<int>.Fail($"Failed to count unread messages: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetMutedAsync(string userId, Guid conversationId, bool muted)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatMember? me = await ctx.ChatMembers.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == userId && x.LeftUtc == null);

            if (me is null)
            {
                return Result<bool>.Fail("That conversation isn't available.");
            }

            me.IsMuted = muted;
            await ctx.SaveChangesAsync();

            _eventBus.Publish(new ChatEvent(conversationId, [userId], ChatEventKind.ConversationChanged));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mute chat {ConversationId} for {UserId}", conversationId, userId);
            return Result<bool>.Fail($"Couldn't change notifications for this conversation: {ex.Message}");
        }
    }

    public async Task<Result<bool>> BlockAsync(string userId, string otherUserId)
    {
        try
        {
            if (otherUserId == userId)
            {
                return Result<bool>.Fail("You can't block yourself.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool exists = await ctx.ChatBlocks.AnyAsync(x => x.BlockerUserId == userId && x.BlockedUserId == otherUserId);

            if (!exists)
            {
                ctx.ChatBlocks.Add(new ChatBlock { Id = Guid.NewGuid(), BlockerUserId = userId, BlockedUserId = otherUserId, CreatedUtc = Now });

                try
                {
                    await ctx.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Blocked twice at once; the other request saved it.
                }
            }

            _logger.LogInformation("{UserId} blocked {OtherUserId} in chat", userId, otherUserId);
            await PublishDirectAsync(ctx, userId, otherUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to block {OtherUserId} for {UserId}", otherUserId, userId);
            return Result<bool>.Fail($"Couldn't block them: {ex.Message}");
        }
    }

    public async Task<Result<bool>> UnblockAsync(string userId, string otherUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ChatBlock> blocks = await ctx.ChatBlocks.Where(x => x.BlockerUserId == userId && x.BlockedUserId == otherUserId).ToListAsync();
            ctx.ChatBlocks.RemoveRange(blocks);
            await ctx.SaveChangesAsync();

            await PublishDirectAsync(ctx, userId, otherUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unblock {OtherUserId} for {UserId}", otherUserId, userId);
            return Result<bool>.Fail($"Couldn't unblock them: {ex.Message}");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static string? ValidateGroupName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the group a name."
        : name.Trim().Length > MaxGroupNameLength ? $"Group names can be up to {MaxGroupNameLength} characters."
        : null;

    // The conversation and the person's membership, or nulls when they aren't a current member.
    private static async Task<(ChatConversation?, ChatMember?)> MembershipAsync(ApplicationDbContext ctx, string userId, Guid conversationId, bool tracked)
    {
        IQueryable<ChatMember> members = tracked ? ctx.ChatMembers : ctx.ChatMembers.AsNoTracking();

        ChatMember? member = await members
            .TagWithCallSite()
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == userId && x.LeftUtc == null && x.Conversation!.IsActive);

        return (member?.Conversation, member);
    }

    private async Task<string?> CannotPostReasonAsync(ApplicationDbContext ctx, string userId, ChatConversation conversation, string? otherUserId)
    {
        if (await ChatRules.SuspendedUntilAsync(ctx, userId, conversation.CompanyId, Now) is { } suspended)
        {
            return suspended;
        }

        if (otherUserId is not null && await ChatRules.BlockedEitherWayAsync(ctx, userId, otherUserId))
        {
            return "You can't send messages in this conversation.";
        }

        return null;
    }

    private static async Task<Dictionary<Guid, int>> UnreadByConversationAsync(ApplicationDbContext ctx, string userId, List<Guid> conversationIds)
    {
        if (conversationIds.Count == 0)
        {
            return [];
        }

        var counts = await ctx.ChatMessages
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => conversationIds.Contains(x.ConversationId) && x.AuthorUserId != userId && x.DeletedUtc == null)
            .Join(ctx.ChatMembers.Where(m => m.UserId == userId && m.LeftUtc == null),
                message => message.ConversationId,
                member => member.ConversationId,
                (message, member) => new { message.ConversationId, message.CreateUtc, Since = member.LastReadUtc ?? member.JoinedUtc })
            .Where(x => x.CreateUtc > x.Since)
            .GroupBy(x => x.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(x => x.ConversationId, x => x.Count);
    }

    // Groups by their name; direct conversations by the other person's name.
    private static async Task<Dictionary<Guid, string>> TitlesAsync(ApplicationDbContext ctx, string userId, List<ChatConversation> conversations)
    {
        List<Guid> directIds = conversations.Where(x => x.Kind == ChatConversationKind.Direct).Select(x => x.Id).ToList();

        var others = await ctx.ChatMembers
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.User)
            .Where(x => directIds.Contains(x.ConversationId) && x.UserId != userId)
            .Select(x => new { x.ConversationId, x.User })
            .ToListAsync();

        return conversations.ToDictionary(
            x => x.Id,
            x => x.Kind == ChatConversationKind.Direct
                ? RotaNames.For(others.FirstOrDefault(o => o.ConversationId == x.Id)?.User)
                : x.Name ?? "Group");
    }

    private async Task PublishAsync(ApplicationDbContext ctx, Guid conversationId, ChatEventKind kind)
    {
        List<string> members = await ctx.ChatMembers
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.ConversationId == conversationId && x.LeftUtc == null)
            .Select(x => x.UserId)
            .ToListAsync();

        _eventBus.Publish(new ChatEvent(conversationId, members, kind));
    }

    private async Task PublishDirectAsync(ApplicationDbContext ctx, string userId, string otherUserId)
    {
        string key = ChatRules.DirectKey(userId, otherUserId);
        Guid? id = await ctx.ChatConversations.AsNoTracking().TagWithCallSite().Where(x => x.DirectKey == key).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();

        if (id is Guid conversationId)
        {
            _eventBus.Publish(new ChatEvent(conversationId, [userId, otherUserId], ChatEventKind.ConversationChanged));
        }
    }
}
