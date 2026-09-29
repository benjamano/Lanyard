using Lanyard.Application.Services.Chat;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Chat;

// The two-year limit on chat: old messages, resolved reports, ended suspensions and conversations
// with nothing left go; anything newer, still under review, pinned, or a channel stays.
[TestClass]
public class ChatRetentionPurgeTests
{
    private static readonly DateTime Now = new(2028, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddYears(-2).AddDays(-1);
    private static readonly DateTime Recent = Now.AddYears(-2).AddDays(1);

    private static ChatConversation Conversation(ChatConversationKind kind, DateTime lastMessageUtc) =>
        new() { Id = Guid.NewGuid(), CompanyId = 1, Kind = kind, CreateDate = lastMessageUtc, LastMessageUtc = lastMessageUtc };

    private static ChatMessage Message(ChatConversation conversation, DateTime sentUtc, string text = "Hello") =>
        new() { Id = Guid.NewGuid(), ConversationId = conversation.Id, AuthorUserId = "amy", BodyHtml = $"<p>{text}</p>", BodyText = text, CreateUtc = sentUtc };

    private static ChatReport Report(ChatMessage message, ChatReportStatus status, DateTime? reviewedUtc) =>
        new()
        {
            Id = Guid.NewGuid(), MessageId = message.Id, ReporterUserId = "tom", ReportedUserId = "amy",
            MessageHtmlSnapshot = message.BodyHtml, MessageSentUtc = message.CreateUtc, LocationId = 1,
            CreatedUtc = message.CreateUtc, Status = status, ReviewedUtc = reviewedUtc
        };

    private static async Task<ChatRetention.PurgeResult> PurgeAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ChatRetention.PurgeExpiredAsync(ctx, Now, batchSize: 2);
    }

    [TestMethod]
    public async Task DeletesMessagesOlderThanTwoYears_KeepsNewerAndPinned()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        ChatConversation group = Conversation(ChatConversationKind.Group, Recent);
        ChatMessage[] old = [Message(group, Old), Message(group, Old.AddDays(-10)), Message(group, Old.AddDays(-20))];
        ChatMessage recent = Message(group, Recent);
        ChatMessage pinned = Message(group, Old.AddDays(-30));
        pinned.IsPinned = true;
        ChatMessage reply = Message(group, Recent);
        reply.ReplyToMessageId = old[0].Id;

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ChatConversations.Add(group);
            ctx.ChatMessages.AddRange([.. old, recent, pinned, reply]);
            await ctx.SaveChangesAsync();
        }

        ChatRetention.PurgeResult result = await PurgeAsync(options);

        Assert.AreEqual(3, result.MessagesDeleted);

        await using ApplicationDbContext verify = new(options);
        List<Guid> left = await verify.ChatMessages.Select(x => x.Id).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { recent.Id, pinned.Id, reply.Id }, left);
        Assert.IsNull((await verify.ChatMessages.SingleAsync(x => x.Id == reply.Id)).ReplyToMessageId, "The reply stays, without its quote");
        Assert.AreEqual(1, await verify.ChatConversations.CountAsync(), "The group still has messages");
    }

    [TestMethod]
    public async Task Reports_GoTwoYearsAfterResolution_AndTheirMessageIsEmptiedMeanwhile()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        ChatConversation direct = Conversation(ChatConversationKind.Direct, Recent);
        ChatMessage resolvedLongAgo = Message(direct, Old.AddDays(-100), "Old and dealt with");
        ChatMessage resolvedRecently = Message(direct, Old.AddDays(-100), "Old, dealt with lately");
        ChatMessage stillOpen = Message(direct, Old.AddDays(-100), "Old, never reviewed");
        ChatReport longAgo = Report(resolvedLongAgo, ChatReportStatus.Dismissed, Old);
        ChatReport lately = Report(resolvedRecently, ChatReportStatus.ActionTaken, Recent);
        ChatReport open = Report(stillOpen, ChatReportStatus.Open, null);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ChatConversations.Add(direct);
            ctx.ChatMessages.AddRange(resolvedLongAgo, resolvedRecently, stillOpen);
            ctx.ChatReports.AddRange(longAgo, lately, open);
            await ctx.SaveChangesAsync();
        }

        ChatRetention.PurgeResult result = await PurgeAsync(options);

        Assert.AreEqual(1, result.Reports);
        Assert.AreEqual(1, result.MessagesDeleted);
        Assert.AreEqual(2, result.MessagesEmptied);

        await using ApplicationDbContext verify = new(options);
        CollectionAssert.AreEquivalent(new[] { lately.Id, open.Id }, await verify.ChatReports.Select(x => x.Id).ToListAsync());
        Assert.IsFalse(await verify.ChatMessages.AnyAsync(x => x.Id == resolvedLongAgo.Id));

        // The kept reports still have what was said; the messages themselves don't.
        Assert.AreEqual("<p>Old, never reviewed</p>", (await verify.ChatReports.SingleAsync(x => x.Id == open.Id)).MessageHtmlSnapshot);
        Assert.IsTrue(await verify.ChatMessages.Where(x => x.Id == stillOpen.Id || x.Id == resolvedRecently.Id).AllAsync(x => x.BodyText == "" && x.DeletedUtc != null));
    }

    [TestMethod]
    public async Task EmptyOldConversationsGo_ChannelsAndRecentOnesStay()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        ChatConversation oldDirect = Conversation(ChatConversationKind.Direct, Old);
        ChatConversation oldGroup = Conversation(ChatConversationKind.Group, Old);
        ChatConversation newGroup = Conversation(ChatConversationKind.Group, Recent);
        ChatConversation quietChannel = Conversation(ChatConversationKind.LocationChannel, Old);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ChatConversations.AddRange(oldDirect, oldGroup, newGroup, quietChannel);
            ctx.ChatMessages.Add(Message(oldDirect, Old));
            ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = oldGroup.Id, UserId = "amy", JoinedUtc = Old });
            await ctx.SaveChangesAsync();
        }

        ChatRetention.PurgeResult result = await PurgeAsync(options);

        Assert.AreEqual(2, result.Conversations);

        await using ApplicationDbContext verify = new(options);
        CollectionAssert.AreEquivalent(new[] { newGroup.Id, quietChannel.Id }, await verify.ChatConversations.Select(x => x.Id).ToListAsync());
        Assert.AreEqual(0, await verify.ChatMembers.CountAsync());
    }

    [TestMethod]
    public async Task Suspensions_GoTwoYearsAfterTheyEnded()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        ChatSuspension ranOut = new() { Id = Guid.NewGuid(), UserId = "amy", CompanyId = 1, CreatedUtc = Old.AddDays(-7), UntilUtc = Old, Reason = "Spam", ImposedByUserId = "sam" };
        ChatSuspension liftedLongAgo = new() { Id = Guid.NewGuid(), UserId = "tom", CompanyId = 1, CreatedUtc = Old.AddDays(-7), Reason = "Spam", ImposedByUserId = "sam", LiftedUtc = Old };
        ChatSuspension endedLately = new() { Id = Guid.NewGuid(), UserId = "priya", CompanyId = 1, CreatedUtc = Old, UntilUtc = Recent, Reason = "Spam", ImposedByUserId = "sam" };
        ChatSuspension indefinite = new() { Id = Guid.NewGuid(), UserId = "jo", CompanyId = 1, CreatedUtc = Old.AddYears(-1), Reason = "Harassment", ImposedByUserId = "sam" };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.ChatSuspensions.AddRange(ranOut, liftedLongAgo, endedLately, indefinite);
            await ctx.SaveChangesAsync();
        }

        ChatRetention.PurgeResult result = await PurgeAsync(options);

        Assert.AreEqual(2, result.Suspensions);

        await using ApplicationDbContext verify = new(options);
        CollectionAssert.AreEquivalent(new[] { endedLately.Id, indefinite.Id }, await verify.ChatSuspensions.Select(x => x.Id).ToListAsync());
    }
}
