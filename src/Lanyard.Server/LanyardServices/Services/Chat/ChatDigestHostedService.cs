using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Chat;

// Once an hour, emails anyone with messages that have sat unread for a day - one email per person,
// naming the conversations and how many are waiting, never what they say. Each message is
// mentioned once: the member row remembers the newest message an email has covered.
public class ChatDigestHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ChatDigestHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan UnreadFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChatDigestHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChatDigestHostedService started");

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(Interval, _timeProvider);

            do
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();

                    int sent = await SweepAsync(
                        scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                        scope.ServiceProvider.GetRequiredService<INotificationDispatcher>(),
                        _timeProvider.GetUtcNow().UtcDateTime);

                    if (sent > 0)
                    {
                        _logger.LogInformation("Queued {Count} unread-chat emails", sent);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unread-chat email sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("ChatDigestHostedService stopped");
    }

    // Public so tests can run a sweep at a chosen moment. Returns how many people were emailed.
    public static async Task<int> SweepAsync(IDbContextFactory<ApplicationDbContext> factory, INotificationDispatcher notifications, DateTime nowUtc)
    {
        DateTime cutoff = nowUtc - UnreadFor;

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

        // Per membership: messages from others, still there, unread, older than a day, and newer
        // than anything a previous email covered. Muted conversations are left out.
        var waiting = await ctx.ChatMembers
            .AsNoTracking()
            .TagWithCallSite()
            .Where(m => m.LeftUtc == null && !m.IsMuted && m.Conversation!.IsActive)
            .Join(ctx.ChatMessages, m => m.ConversationId, msg => msg.ConversationId, (m, msg) => new { Member = m, msg })
            .Where(x => x.msg.AuthorUserId != x.Member.UserId
                && x.msg.DeletedUtc == null
                && x.msg.CreateUtc <= cutoff
                && x.msg.CreateUtc > (x.Member.LastReadUtc ?? x.Member.JoinedUtc)
                && (x.Member.LastDigestEmailUtc == null || x.msg.CreateUtc > x.Member.LastDigestEmailUtc))
            .GroupBy(x => new { x.Member.Id, x.Member.UserId, x.Member.ConversationId })
            .Select(g => new { g.Key.Id, g.Key.UserId, g.Key.ConversationId, Count = g.Count(), Newest = g.Max(x => x.msg.CreateUtc) })
            .ToListAsync();

        if (waiting.Count == 0)
        {
            return 0;
        }

        List<Guid> conversationIds = waiting.Select(x => x.ConversationId).Distinct().ToList();

        Dictionary<Guid, ChatConversation> conversations = await ctx.ChatConversations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => conversationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        var directMembers = await ctx.ChatMembers
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.User)
            .Where(x => conversationIds.Contains(x.ConversationId) && x.Conversation!.Kind == ChatConversationKind.Direct)
            .Select(x => new { x.ConversationId, x.UserId, x.User })
            .ToListAsync();

        int emailed = 0;

        foreach (var person in waiting.GroupBy(x => x.UserId))
        {
            List<ChatDigestLine> lines = person
                .OrderByDescending(x => x.Newest)
                .Select(x =>
                {
                    ChatConversation conversation = conversations[x.ConversationId];
                    string name = conversation.Kind == ChatConversationKind.Direct
                        ? RotaNames.For(directMembers.FirstOrDefault(d => d.ConversationId == x.ConversationId && d.UserId != person.Key)?.User)
                        : conversation.Name ?? "Group";

                    return new ChatDigestLine(name, x.Count);
                })
                .ToList();

            notifications.Enqueue([person.Key], NotificationTopic.ChatUnreadEmail, new ChatDigestPayload(lines));
            emailed++;
        }

        // Remember what's been covered, so the same messages aren't emailed about again.
        List<Guid> memberIds = waiting.Select(x => x.Id).ToList();
        List<ChatMember> members = await ctx.ChatMembers.Where(x => memberIds.Contains(x.Id)).ToListAsync();

        foreach (ChatMember member in members)
        {
            member.LastDigestEmailUtc = waiting.First(x => x.Id == member.Id).Newest;
        }

        await ctx.SaveChangesAsync();

        return emailed;
    }
}
