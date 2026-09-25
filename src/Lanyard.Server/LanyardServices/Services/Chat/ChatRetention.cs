using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Chat;

// What happens to someone's chat when their account is deleted. Their memberships, blocks and
// suspensions go with the account (cascading keys). What they wrote in groups stays, attributed
// to the placeholder account, so the conversation still makes sense to everyone else; reports
// they made or that are about them keep their snapshot for the retention period, with the person
// re-pointed to the placeholder. A GDPR erasure also empties the direct messages they sent.
// Run by ScheduleRetention.DetachUserAsync; the snapshot lets a failed account delete be undone.
public static class ChatRetention
{
    public record MessageFields(Guid Id, string AuthorUserId, string BodyHtml, string BodyText, DateTime? DeletedUtc, string? DeletedByUserId);

    public record ReportFields(Guid Id, string ReporterUserId, string ReportedUserId, string? ReviewedByUserId);

    public record Snapshot(List<MessageFields> Messages, List<ReportFields> Reports)
    {
        public static readonly Snapshot Empty = new([], []);

        public int Count => Messages.Count + Reports.Count;
    }

    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    public static async Task<Snapshot> DetachUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc, bool eraseDirectMessages)
    {
        List<ChatMessage> messages = await ctx.ChatMessages
            .Include(x => x.Conversation)
            .Where(x => x.AuthorUserId == userId || x.DeletedByUserId == userId)
            .ToListAsync();

        List<ChatReport> reports = await ctx.ChatReports
            .Where(x => x.ReporterUserId == userId || x.ReportedUserId == userId || x.ReviewedByUserId == userId)
            .ToListAsync();

        Snapshot snapshot = new(
            messages.Select(x => new MessageFields(x.Id, x.AuthorUserId, x.BodyHtml, x.BodyText, x.DeletedUtc, x.DeletedByUserId)).ToList(),
            reports.Select(x => new ReportFields(x.Id, x.ReporterUserId, x.ReportedUserId, x.ReviewedByUserId)).ToList());

        foreach (ChatMessage message in messages)
        {
            if (message.AuthorUserId == userId)
            {
                if (eraseDirectMessages && message.Conversation?.Kind == Infrastructure.Enum.ChatConversationKind.Direct && !message.IsDeleted)
                {
                    ChatRules.Erase(message, null, nowUtc);
                }

                message.AuthorUserId = Placeholder;
            }

            if (message.DeletedByUserId == userId)
            {
                message.DeletedByUserId = null;
            }
        }

        foreach (ChatReport report in reports)
        {
            if (report.ReporterUserId == userId)
            {
                report.ReporterUserId = Placeholder;
            }

            if (report.ReportedUserId == userId)
            {
                report.ReportedUserId = Placeholder;
            }

            if (report.ReviewedByUserId == userId)
            {
                report.ReviewedByUserId = null;
            }
        }

        return snapshot;
    }

    // ---- The two-year limit ------------------------------------------------------------------

    public const int KeepForYears = 2;

    public record PurgeResult(int MessagesDeleted, int MessagesEmptied, int Reports, int Suspensions, int Conversations)
    {
        public int Total => MessagesDeleted + MessagesEmptied + Reports + Suspensions + Conversations;
    }

    // Chat is kept for two years (docs/DATA_RETENTION.md), then deleted:
    // - reports two years after they were resolved (an open report waits for a manager);
    // - suspensions two years after they ended;
    // - messages two years after they were sent, except a pinned post, which stays until it's
    //   unpinned. A message a kept report points at is emptied instead, since the report
    //   carries its own snapshot and goes later;
    // - direct conversations and groups with nothing left in them and no messages for two years.
    // Channels themselves stay. Run daily by ChatRetentionHostedService.
    public static async Task<PurgeResult> PurgeExpiredAsync(ApplicationDbContext ctx, DateTime nowUtc, int batchSize = 500)
    {
        DateTime cutoff = nowUtc.AddYears(-KeepForYears);

        List<ChatReport> reports = await ctx.ChatReports
            .TagWithCallSite()
            .Where(x => x.Status != ChatReportStatus.Open && x.ReviewedUtc != null && x.ReviewedUtc < cutoff)
            .ToListAsync();

        List<ChatSuspension> suspensions = await ctx.ChatSuspensions
            .TagWithCallSite()
            .Where(x => x.LiftedUtc != null ? x.LiftedUtc < cutoff : x.UntilUtc != null && x.UntilUtc < cutoff)
            .ToListAsync();

        ctx.ChatReports.RemoveRange(reports);
        ctx.ChatSuspensions.RemoveRange(suspensions);
        await ctx.SaveChangesAsync();

        List<ChatMessage> evidence = await ctx.ChatMessages
            .TagWithCallSite()
            .Where(m => m.CreateUtc < cutoff && !m.IsPinned && m.DeletedUtc == null && ctx.ChatReports.Any(r => r.MessageId == m.Id))
            .ToListAsync();

        foreach (ChatMessage message in evidence)
        {
            ChatRules.Erase(message, null, nowUtc);
        }

        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        int deleted = 0;

        while (true)
        {
            List<ChatMessage> batch = await ctx.ChatMessages
                .TagWithCallSite()
                .Where(m => m.CreateUtc < cutoff && !m.IsPinned && !ctx.ChatReports.Any(r => r.MessageId == m.Id))
                .OrderBy(m => m.CreateUtc)
                .Take(batchSize)
                .ToListAsync();

            if (batch.Count == 0)
            {
                break;
            }

            // Replies to a deleted message lose the quote, not themselves.
            List<Guid> ids = batch.Select(x => x.Id).ToList();
            List<ChatMessage> replies = await ctx.ChatMessages
                .Where(m => m.ReplyToMessageId != null && ids.Contains(m.ReplyToMessageId.Value))
                .ToListAsync();

            foreach (ChatMessage reply in replies)
            {
                reply.ReplyToMessageId = null;
            }

            ctx.ChatMessages.RemoveRange(batch);
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();

            deleted += batch.Count;
        }

        List<ChatConversation> empty = await ctx.ChatConversations
            .TagWithCallSite()
            .Where(c => (c.Kind == ChatConversationKind.Direct || c.Kind == ChatConversationKind.Group)
                && c.LastMessageUtc < cutoff
                && !ctx.ChatMessages.Any(m => m.ConversationId == c.Id))
            .ToListAsync();

        List<Guid> emptyIds = empty.Select(x => x.Id).ToList();
        ctx.ChatMembers.RemoveRange(await ctx.ChatMembers.Where(x => emptyIds.Contains(x.ConversationId)).ToListAsync());
        ctx.ChatConversations.RemoveRange(empty);
        await ctx.SaveChangesAsync();

        return new PurgeResult(deleted, evidence.Count, reports.Count, suspensions.Count, empty.Count);
    }

    public static async Task RestoreAsync(ApplicationDbContext ctx, Snapshot snapshot)
    {
        List<Guid> messageIds = snapshot.Messages.Select(x => x.Id).ToList();
        Dictionary<Guid, ChatMessage> messages = await ctx.ChatMessages.Where(x => messageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (MessageFields original in snapshot.Messages)
        {
            if (messages.TryGetValue(original.Id, out ChatMessage? message))
            {
                message.AuthorUserId = original.AuthorUserId;
                message.BodyHtml = original.BodyHtml;
                message.BodyText = original.BodyText;
                message.DeletedUtc = original.DeletedUtc;
                message.DeletedByUserId = original.DeletedByUserId;
            }
        }

        List<Guid> reportIds = snapshot.Reports.Select(x => x.Id).ToList();
        Dictionary<Guid, ChatReport> reports = await ctx.ChatReports.Where(x => reportIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (ReportFields original in snapshot.Reports)
        {
            if (reports.TryGetValue(original.Id, out ChatReport? report))
            {
                report.ReporterUserId = original.ReporterUserId;
                report.ReportedUserId = original.ReportedUserId;
                report.ReviewedByUserId = original.ReviewedByUserId;
            }
        }
    }
}
