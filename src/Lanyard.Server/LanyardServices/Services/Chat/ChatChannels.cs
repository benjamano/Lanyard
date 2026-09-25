using System.Net;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Chat;

// Channels: one per location ("Peterborough team") and one per company ("Partyman everyone"),
// created the first time they're needed. Who's in a channel follows who works where
// (UserLocationMemberships) rather than anyone adding people: membership rows exist for read
// markers and muting, and are brought up to date whenever someone opens chat and before anything
// is sent to a channel.
public static class ChatChannels
{
    public static bool IsChannel(ChatConversation conversation) =>
        conversation.Kind is ChatConversationKind.LocationChannel or ChatConversationKind.CompanyChannel;

    // Who looks after a channel - pins, removes, posts while staff posting is off, and sees it on
    // Manage > Chat Channels: an Admin any channel; a Manager only their signed-in location's
    // channel (SchedulingAccess.CanManageLocation, the rota's rule). No scope, no rights.
    public static bool CanModerate(LocationScope? scope, ChatConversation channel) => scope is not null && channel.Kind switch
    {
        ChatConversationKind.LocationChannel => channel.LocationId is int locationId && SchedulingAccess.CanManageLocation(scope, locationId),
        ChatConversationKind.CompanyChannel => scope.IsAdmin,
        _ => false
    };

    public static async Task<ChatConversation> GetOrCreateLocationChannelAsync(ApplicationDbContext ctx, Location location, DateTime nowUtc)
    {
        ChatConversation? existing = await ctx.ChatConversations
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.Kind == ChatConversationKind.LocationChannel && x.LocationId == location.Id);

        return existing ?? await CreateAsync(ctx, new ChatConversation
        {
            Id = Guid.NewGuid(),
            CompanyId = location.CompanyId,
            LocationId = location.Id,
            Kind = ChatConversationKind.LocationChannel,
            Name = $"{location.Name} team",
            CreateDate = nowUtc,
            LastMessageUtc = nowUtc
        }, x => x.Kind == ChatConversationKind.LocationChannel && x.LocationId == location.Id);
    }

    public static async Task<ChatConversation> GetOrCreateCompanyChannelAsync(ApplicationDbContext ctx, Company company, DateTime nowUtc)
    {
        ChatConversation? existing = await ctx.ChatConversations
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.Kind == ChatConversationKind.CompanyChannel && x.CompanyId == company.Id);

        return existing ?? await CreateAsync(ctx, new ChatConversation
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Kind = ChatConversationKind.CompanyChannel,
            Name = $"{company.Name} everyone",
            CreateDate = nowUtc,
            LastMessageUtc = nowUtc
        }, x => x.Kind == ChatConversationKind.CompanyChannel && x.CompanyId == company.Id);
    }

    // Two people opening chat at once can both try to create the channel; the unique index lets
    // one win and the other uses it.
    private static async Task<ChatConversation> CreateAsync(ApplicationDbContext ctx, ChatConversation channel, System.Linq.Expressions.Expression<Func<ChatConversation, bool>> same)
    {
        ctx.ChatConversations.Add(channel);

        try
        {
            await ctx.SaveChangesAsync();
            return channel;
        }
        catch (DbUpdateException)
        {
            ctx.Entry(channel).State = EntityState.Detached;
            return await ctx.ChatConversations.FirstAsync(same);
        }
    }

    // The channels of every active location the person works at, and of those locations' companies,
    // with the person's membership brought up to date (joined where they work now, left where they
    // no longer do).
    public static async Task EnsureChannelsForUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc)
    {
        List<Location> locations = await ctx.Locations
            .TagWithCallSite()
            .Include(x => x.Company)
            .Where(x => x.IsActive && ctx.UserLocationMemberships.Any(m => m.UserId == userId && m.LocationId == x.Id))
            .ToListAsync();

        List<Guid> channelIds = [];

        foreach (Location location in locations)
        {
            channelIds.Add((await GetOrCreateLocationChannelAsync(ctx, location, nowUtc)).Id);
        }

        foreach (Company company in locations.Select(x => x.Company!).Where(x => x is not null).DistinctBy(x => x.Id))
        {
            channelIds.Add((await GetOrCreateCompanyChannelAsync(ctx, company, nowUtc)).Id);
        }

        List<ChatMember> memberships = await ctx.ChatMembers
            .Include(x => x.Conversation)
            .Where(x => x.UserId == userId
                && (x.Conversation!.Kind == ChatConversationKind.LocationChannel || x.Conversation.Kind == ChatConversationKind.CompanyChannel))
            .ToListAsync();

        foreach (Guid channelId in channelIds)
        {
            Join(ctx, memberships.FirstOrDefault(x => x.ConversationId == channelId), channelId, userId, nowUtc);
        }

        foreach (ChatMember stale in memberships.Where(x => x.LeftUtc == null && !channelIds.Contains(x.ConversationId)))
        {
            stale.LeftUtc = nowUtc;
        }

        await ctx.SaveChangesAsync();
    }

    // Everyone who should be in the channel right now is, and nobody else. Run before sending to a
    // channel so a new starter who hasn't opened chat yet still gets pinned posts.
    public static async Task<List<string>> SyncMembersAsync(ApplicationDbContext ctx, ChatConversation channel, DateTime nowUtc)
    {
        IQueryable<string> eligibleQuery = channel.Kind == ChatConversationKind.LocationChannel
            ? ctx.UserLocationMemberships.Where(x => x.LocationId == channel.LocationId).Select(x => x.UserId)
            : ChatRules.CompanyMemberIds(ctx, channel.CompanyId);

        List<string> eligible = (await eligibleQuery.Distinct().ToListAsync())
            .Where(x => x != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .ToList();

        List<ChatMember> rows = await ctx.ChatMembers.Where(x => x.ConversationId == channel.Id).ToListAsync();

        foreach (string userId in eligible)
        {
            Join(ctx, rows.FirstOrDefault(x => x.UserId == userId), channel.Id, userId, nowUtc);
        }

        foreach (ChatMember gone in rows.Where(x => x.LeftUtc == null && !eligible.Contains(x.UserId)))
        {
            gone.LeftUtc = nowUtc;
        }

        await ctx.SaveChangesAsync();

        return eligible;
    }

    // Joining a channel starts with it read: someone new doesn't arrive to a hundred unread
    // messages, though (unlike a group) they can scroll back through the channel's history.
    private static void Join(ApplicationDbContext ctx, ChatMember? existing, Guid channelId, string userId, DateTime nowUtc)
    {
        if (existing is null)
        {
            ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = channelId, UserId = userId, JoinedUtc = nowUtc, LastReadUtc = nowUtc });
        }
        else if (existing.LeftUtc is not null)
        {
            existing.LeftUtc = null;
            existing.JoinedUtc = nowUtc;
            existing.LastReadUtc = nowUtc;
        }
    }

    // Everyone in the channel but the person who pinned it hears about a pinned post, even with
    // the channel muted - it's the managers' news, not chatter.
    public static async Task NotifyPinnedAsync(ApplicationDbContext ctx, INotificationDispatcher notifications, ChatConversation channel, ChatMessage message, IReadOnlyCollection<string> members, string pinnedByUserId)
    {
        string author = RotaNames.For(await ctx.Users.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == message.AuthorUserId));

        notifications.Enqueue(members.Where(x => x != pinnedByUserId), NotificationTopic.PinnedPost,
            new PinnedPostPayload(channel.Id, channel.Name ?? "Channel", author, ChatHtml.Preview(message.BodyText)));
    }

    // Posts a card (an open shift, a swap request) into a location's channel. Text is what shows
    // in previews; the thread draws the card from its live state. Skipped if the same thing
    // already has a card there, so re-announcing a shift doesn't repeat it.
    public static async Task PostCardAsync(ApplicationDbContext ctx, int locationId, ChatMessageKind kind, Guid linkedEntityId, string authorUserId, string text, DateTime nowUtc)
    {
        Location? location = await ctx.Locations.TagWithCallSite().FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

        if (location is null)
        {
            return;
        }

        ChatConversation channel = await GetOrCreateLocationChannelAsync(ctx, location, nowUtc);

        bool already = await ctx.ChatMessages.AnyAsync(x => x.ConversationId == channel.Id && x.Kind == kind && x.LinkedEntityId == linkedEntityId && x.DeletedUtc == null);

        if (already)
        {
            return;
        }

        ctx.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = channel.Id,
            AuthorUserId = authorUserId,
            BodyHtml = $"<p>{WebUtility.HtmlEncode(text)}</p>",
            BodyText = text,
            CreateUtc = nowUtc,
            Kind = kind,
            LinkedEntityId = linkedEntityId
        });

        channel.LastMessageUtc = nowUtc;
        await ctx.SaveChangesAsync();
    }
}
