using Lanyard.Infrastructure.DataAccess;
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
