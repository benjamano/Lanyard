using Lanyard.Application.Services.Chat;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Chat;

// Direct and group messages, and the rules that keep them private: members only, one company, and
// nobody else - Admins included - ever reads a direct conversation. Reporting shares a snapshot of
// the one message, and that's all a manager sees.
[TestClass]
public class ChatServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Location,
        UserProfile Manager,
        UserProfile Amy,
        UserProfile Tom,
        UserProfile Priya,
        TestClock Clock,
        RecordingNotificationDispatcher Notifications,
        ChatPresence Presence,
        ChatService Chat,
        ChatModerationService Moderation);

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");
        UserProfile priya = await SchedulingTestHelpers.SeedUserAsync(options, location, "Priya");

        await using (ApplicationDbContext ctx = new(options))
        {
            ApplicationRole role = new() { Id = Guid.NewGuid().ToString(), Name = "Manager", NormalizedName = "MANAGER", CreatedByUserId = "seed", IsActive = true };
            ctx.Roles.Add(role);
            ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = manager.Id, RoleId = role.Id });
            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(Now);
        RecordingNotificationDispatcher notifications = new();
        ChatEventBus bus = new();
        ChatPresence presence = new();
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);

        return new World(options, company, location, manager, amy, tom, priya, clock, notifications, presence,
            new ChatService(factory, notifications, bus, presence, clock, NullLogger<ChatService>.Instance),
            new ChatModerationService(factory, notifications, bus, clock, NullLogger<ChatModerationService>.Instance));
    }

    private static async Task<ChatConversation> DirectAsync(World w, UserProfile a, UserProfile b) =>
        (await w.Chat.GetOrCreateDirectAsync(a.Id, w.Company.Id, b.Id)).Data!;

    private static async Task<ChatMessage> SendAsync(World w, UserProfile from, ChatConversation conversation, string html = "<p>Hello</p>")
    {
        Result<ChatMessage> sent = await w.Chat.SendAsync(from.Id, conversation.Id, html);
        Assert.IsTrue(sent.IsSuccess, sent.Error);
        return sent.Data!;
    }

    // ---- Conversations ---------------------------------------------------------------------

    [TestMethod]
    public async Task Direct_IsReused_WhoeverStartsIt()
    {
        World w = await SeedAsync();

        ChatConversation first = await DirectAsync(w, w.Amy, w.Tom);
        ChatConversation second = await DirectAsync(w, w.Tom, w.Amy);

        Assert.AreEqual(first.Id, second.Id);
    }

    [TestMethod]
    public async Task Direct_CantReachAnotherCompany()
    {
        World w = await SeedAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Leeds");
        UserProfile outsider = await SchedulingTestHelpers.SeedUserAsync(w.Options, elsewhere, "Olly");

        Result<ChatConversation> result = await w.Chat.GetOrCreateDirectAsync(w.Amy.Id, w.Company.Id, outsider.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Group_NeedsANameAndPeopleFromTheCompany()
    {
        World w = await SeedAsync();

        Assert.IsFalse((await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, " ", [w.Tom.Id])).IsSuccess);
        Assert.IsFalse((await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [])).IsSuccess);

        Result<ChatConversation> group = await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Weekend crew", [w.Tom.Id, w.Priya.Id]);

        Assert.IsTrue(group.IsSuccess, group.Error);
        ChatThread thread = (await w.Chat.GetThreadAsync(w.Tom.Id, group.Data!.Id)).Data!;
        Assert.AreEqual(3, thread.Members.Count);
        Assert.AreEqual("Weekend crew", thread.Title);
    }

    [TestMethod]
    public async Task Group_LeavingStopsReading_AndAddingBackWorks()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;

        await w.Chat.LeaveGroupAsync(w.Tom.Id, group.Id);
        Assert.IsFalse((await w.Chat.GetThreadAsync(w.Tom.Id, group.Id)).IsSuccess);
        Assert.IsFalse((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Hi</p>")).IsSuccess);

        await w.Chat.AddMembersAsync(w.Amy.Id, group.Id, [w.Tom.Id]);
        Assert.IsTrue((await w.Chat.GetThreadAsync(w.Tom.Id, group.Id)).IsSuccess);
    }

    // ---- Privacy ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Direct_OnlyItsTwoMembersCanRead_AdminsIncluded()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        await SendAsync(w, w.Amy, direct, "<p>Private</p>");

        // The manager (and there is no admin route at all) gets the same answer as for a
        // conversation that doesn't exist.
        Result<ChatThread> manager = await w.Chat.GetThreadAsync(w.Manager.Id, direct.Id);
        Result<ChatThread> missing = await w.Chat.GetThreadAsync(w.Manager.Id, Guid.NewGuid());

        Assert.IsFalse(manager.IsSuccess);
        Assert.AreEqual(missing.Error, manager.Error);
        Assert.IsFalse((await w.Chat.SendAsync(w.Priya.Id, direct.Id, "<p>Hi</p>")).IsSuccess);
        Assert.AreEqual(0, (await w.Chat.GetInboxAsync(w.Manager.Id)).Data!.Count(x => x.Kind == ChatConversationKind.Direct));
    }

    [TestMethod]
    public async Task Report_SharesOnlyASnapshotOfThatMessage_WhichOutlivesIt()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        await SendAsync(w, w.Amy, direct, "<p>Fine message</p>");
        ChatMessage nasty = await SendAsync(w, w.Tom, direct, "<p>Nasty message</p>");
        await SendAsync(w, w.Tom, direct, "<p>Another one</p>");

        Result<ChatReport> reported = await w.Moderation.ReportAsync(w.Amy.Id, nasty.Id, ChatReportReason.Harassment, "Not the first time", alsoBlock: false);
        Assert.IsTrue(reported.IsSuccess, reported.Error);

        // Tom deletes it afterwards; the manager still sees what was reported, and nothing else.
        await w.Chat.DeleteOwnAsync(w.Tom.Id, nasty.Id);

        List<ChatReportView> reports = (await w.Moderation.GetReportsAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), w.Location.Id, openOnly: true, w.Manager.Id)).Data!;
        ChatReportView view = reports.Single();

        Assert.AreEqual("<p>Nasty message</p>", view.Report.MessageHtmlSnapshot);
        Assert.IsNull(view.Report.Message, "The live message (and through it the conversation) isn't handed out");
        Assert.IsFalse(view.MessageStillVisible);
        Assert.IsTrue(view.WasDirectMessage);
        CollectionAssert.Contains(w.Notifications.Jobs.Where(x => x.Topic == NotificationTopic.ChatReport).Select(x => x.UserId).ToList(), w.Manager.Id);
    }

    [TestMethod]
    public async Task Report_OnlyByAMember_AndNotYourOwnMessage()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatMessage message = await SendAsync(w, w.Tom, direct);

        Assert.IsFalse((await w.Moderation.ReportAsync(w.Priya.Id, message.Id, ChatReportReason.Spam, null, false)).IsSuccess);
        Assert.IsFalse((await w.Moderation.ReportAsync(w.Tom.Id, message.Id, ChatReportReason.Spam, null, false)).IsSuccess);
    }

    [TestMethod]
    public async Task Report_ManagerCantReviewAReportAboutThemselves()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Manager);
        ChatMessage message = await SendAsync(w, w.Manager, direct);
        ChatReport report = (await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Inappropriate, null, false)).Data!;

        Result<bool> resolved = await w.Moderation.ResolveAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), report.Id, true, false, null, null, w.Manager.Id);

        Assert.IsFalse(resolved.IsSuccess);
        Assert.AreEqual(0, (await w.Moderation.CountOpenForNavAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), w.Manager.Id)).Data);

        // Not even listed for them - the card would say who reported them.
        Assert.AreEqual(0, (await w.Moderation.GetReportsAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), w.Location.Id, openOnly: false, w.Manager.Id)).Data!.Count);
        Assert.AreEqual(1, (await w.Moderation.GetReportsAsync(SchedulingTestHelpers.AdminScope, w.Location.Id, openOnly: false, "admin")).Data!.Count);
    }

    [TestMethod]
    public async Task Suspension_CantBeLiftedByTheSuspendedManager()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Manager.Id])).Data!;
        ChatMessage message = await SendAsync(w, w.Manager, group);
        ChatReport report = (await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Spam, null, false)).Data!;
        await w.Moderation.ResolveAsync(SchedulingTestHelpers.AdminScope, report.Id, false, true, null, null, "admin");

        ChatSuspensionView mine = (await w.Moderation.GetSuspensionsAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), w.Company.Id)).Data!.Single();

        Assert.IsFalse((await w.Moderation.LiftSuspensionAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), mine.Suspension.Id, w.Manager.Id)).IsSuccess);
        Assert.IsTrue((await w.Moderation.LiftSuspensionAsync(SchedulingTestHelpers.AdminScope, mine.Suspension.Id, "admin")).IsSuccess);
    }

    [TestMethod]
    public async Task Suspended_CantRenameOrAddPeopleEither()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;
        ChatMessage message = await SendAsync(w, w.Tom, group);
        ChatReport report = (await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Spam, null, false)).Data!;
        await w.Moderation.ResolveAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), report.Id, false, true, 7, null, w.Manager.Id);

        Assert.IsFalse((await w.Chat.RenameGroupAsync(w.Tom.Id, group.Id, "Rude name")).IsSuccess);
        Assert.IsFalse((await w.Chat.AddMembersAsync(w.Tom.Id, group.Id, [w.Priya.Id])).IsSuccess);
    }

    [TestMethod]
    public async Task Resolve_RemovesTheMessage_SuspendsTheAuthor_AndTellsTheReporter()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;
        ChatMessage message = await SendAsync(w, w.Tom, group, "<p>Rude</p>");
        ChatReport report = (await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Inappropriate, null, false)).Data!;

        Result<bool> resolved = await w.Moderation.ResolveAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), report.Id,
            removeMessage: true, suspend: true, suspendDays: 7, outcome: "Spoke to him", w.Manager.Id);

        Assert.IsTrue(resolved.IsSuccess, resolved.Error);

        ChatThread thread = (await w.Chat.GetThreadAsync(w.Tom.Id, group.Id)).Data!;
        Assert.IsTrue(thread.Messages.Single().Message.IsDeleted);
        Assert.AreEqual(w.Manager.Id, thread.Messages.Single().Message.DeletedByUserId);
        StringAssert.Contains(thread.CannotPostReason, "can't post");

        // Suspended people read but don't post, and it ends by itself.
        Assert.IsFalse((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Hi</p>")).IsSuccess);
        w.Clock.Advance(TimeSpan.FromDays(8));
        Assert.IsTrue((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Sorry</p>")).IsSuccess);

        CollectionAssert.AreEqual(new[] { w.Amy.Id }, w.Notifications.Jobs.Where(x => x.Topic == NotificationTopic.ChatReportReviewed).Select(x => x.UserId).ToList());
    }

    [TestMethod]
    public async Task Suspension_UntilLifted_EndsWhenLifted()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;
        ChatMessage message = await SendAsync(w, w.Tom, group);
        ChatReport report = (await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Spam, null, false)).Data!;
        await w.Moderation.ResolveAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), report.Id, false, true, null, null, w.Manager.Id);

        w.Clock.Advance(TimeSpan.FromDays(400));
        Assert.IsFalse((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Hi</p>")).IsSuccess);

        ChatSuspensionView suspension = (await w.Moderation.GetSuspensionsAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), w.Company.Id)).Data!.Single();
        await w.Moderation.LiftSuspensionAsync(SchedulingTestHelpers.ManagerScopeFor(w.Location), suspension.Suspension.Id, w.Manager.Id);

        Assert.IsTrue((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Hi</p>")).IsSuccess);
    }

    // ---- Blocking --------------------------------------------------------------------------

    [TestMethod]
    public async Task Block_StopsDirectMessagesBothWays_NotGroups()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;

        await w.Chat.BlockAsync(w.Amy.Id, w.Tom.Id);

        Assert.IsFalse((await w.Chat.SendAsync(w.Tom.Id, direct.Id, "<p>Hi</p>")).IsSuccess);
        Assert.IsFalse((await w.Chat.SendAsync(w.Amy.Id, direct.Id, "<p>Hi</p>")).IsSuccess);
        Assert.IsFalse((await w.Chat.GetOrCreateDirectAsync(w.Tom.Id, w.Company.Id, w.Amy.Id)).IsSuccess);
        Assert.IsTrue((await w.Chat.SendAsync(w.Tom.Id, group.Id, "<p>Hi all</p>")).IsSuccess);

        await w.Chat.UnblockAsync(w.Amy.Id, w.Tom.Id);
        Assert.IsTrue((await w.Chat.SendAsync(w.Tom.Id, direct.Id, "<p>Hi</p>")).IsSuccess);
    }

    [TestMethod]
    public async Task Block_AlsoStopsEditingOldDirectMessages()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatMessage message = await SendAsync(w, w.Tom, direct);

        await w.Chat.BlockAsync(w.Amy.Id, w.Tom.Id);

        Assert.IsFalse((await w.Chat.EditAsync(w.Tom.Id, message.Id, "<p>Something nasty</p>")).IsSuccess);
    }

    [TestMethod]
    public async Task ReAddedMember_StartsFresh_WithoutWhatWasSaidWhileTheyWereOut()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;
        await w.Chat.LeaveGroupAsync(w.Tom.Id, group.Id);

        w.Clock.Advance(TimeSpan.FromMinutes(5));
        await SendAsync(w, w.Amy, group, "<p>About Tom while he's out</p>");

        w.Clock.Advance(TimeSpan.FromMinutes(5));
        await w.Chat.AddMembersAsync(w.Amy.Id, group.Id, [w.Tom.Id]);

        Assert.AreEqual(0, (await w.Chat.GetThreadAsync(w.Tom.Id, group.Id)).Data!.Messages.Count);
        Assert.AreEqual(0, (await w.Chat.GetUnreadTotalAsync(w.Tom.Id)).Data);

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(w, w.Amy, group, "<p>Welcome back</p>");
        Assert.AreEqual("Welcome back", (await w.Chat.GetThreadAsync(w.Tom.Id, group.Id)).Data!.Messages.Single().Message.BodyText);
    }

    [TestMethod]
    public async Task Report_WithBlock_BlocksTheAuthor()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatMessage message = await SendAsync(w, w.Tom, direct);

        await w.Moderation.ReportAsync(w.Amy.Id, message.Id, ChatReportReason.Harassment, null, alsoBlock: true);

        Assert.IsTrue((await w.Chat.GetThreadAsync(w.Amy.Id, direct.Id)).Data!.BlockedByMe);
    }

    // ---- Messages --------------------------------------------------------------------------

    [TestMethod]
    public async Task Send_SanitisesTheHtml()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);

        ChatMessage message = await SendAsync(w, w.Amy, direct,
            "<p onclick=\"steal()\"><strong>Hi</strong> <script>alert(1)</script><a href=\"javascript:alert(1)\">x</a> <a href=\"https://example.com\">link</a></p><iframe src=\"https://evil\"></iframe>");

        StringAssert.Contains(message.BodyHtml, "<strong>Hi</strong>");
        Assert.IsFalse(message.BodyHtml.Contains("script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.BodyHtml.Contains("onclick", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.BodyHtml.Contains("iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.BodyHtml.Contains("javascript", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(message.BodyHtml, "rel=\"noopener noreferrer nofollow\"");
    }

    [TestMethod]
    public async Task Send_RefusesAnEmptyMessage()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);

        Assert.IsFalse((await w.Chat.SendAsync(w.Amy.Id, direct.Id, "<p><br></p>")).IsSuccess);
        Assert.IsFalse((await w.Chat.SendAsync(w.Amy.Id, direct.Id, "<p><strong></strong></p>")).IsSuccess);
    }

    [TestMethod]
    public async Task EditAndDelete_OwnMessagesOnly()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatMessage message = await SendAsync(w, w.Amy, direct);

        Assert.IsFalse((await w.Chat.EditAsync(w.Tom.Id, message.Id, "<p>Hacked</p>")).IsSuccess);
        Assert.IsFalse((await w.Chat.DeleteOwnAsync(w.Tom.Id, message.Id)).IsSuccess);

        Assert.IsTrue((await w.Chat.EditAsync(w.Amy.Id, message.Id, "<p>Hello again</p>")).IsSuccess);
        ChatMessageView edited = (await w.Chat.GetThreadAsync(w.Tom.Id, direct.Id)).Data!.Messages.Single();
        Assert.AreEqual("Hello again", edited.Message.BodyText);
        Assert.IsNotNull(edited.Message.EditedUtc);

        Assert.IsTrue((await w.Chat.DeleteOwnAsync(w.Amy.Id, message.Id)).IsSuccess);
        ChatMessageView deleted = (await w.Chat.GetThreadAsync(w.Tom.Id, direct.Id)).Data!.Messages.Single();
        Assert.IsTrue(deleted.Message.IsDeleted);
        Assert.AreEqual(string.Empty, deleted.Message.BodyHtml, "Deleted means gone, not hidden");
    }

    [TestMethod]
    public async Task Unread_CountsOthersMessagesSinceLastRead()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(w, w.Tom, direct);
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(w, w.Tom, direct);

        Assert.AreEqual(2, (await w.Chat.GetUnreadTotalAsync(w.Amy.Id)).Data);
        Assert.AreEqual(0, (await w.Chat.GetUnreadTotalAsync(w.Tom.Id)).Data);
        Assert.AreEqual(2, (await w.Chat.GetInboxAsync(w.Amy.Id)).Data!.Single(x => x.Kind == ChatConversationKind.Direct).UnreadCount);

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await w.Chat.MarkReadAsync(w.Amy.Id, direct.Id);
        Assert.AreEqual(0, (await w.Chat.GetUnreadTotalAsync(w.Amy.Id)).Data);
    }

    [TestMethod]
    public async Task Send_PushesToMembersNotViewingOrMuted()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id, w.Priya.Id, w.Manager.Id])).Data!;

        await w.Chat.SetMutedAsync(w.Priya.Id, group.Id, true);
        using IDisposable watching = w.Presence.Enter(w.Manager.Id, group.Id);

        await SendAsync(w, w.Amy, group, "<p>Shift swap anyone?</p>");

        NotificationJob job = w.Notifications.Jobs.Single(x => x.Topic == NotificationTopic.GroupMessage);
        Assert.AreEqual(w.Tom.Id, job.UserId);
        ChatMessagePayload payload = (ChatMessagePayload)job.Payload;
        Assert.AreEqual("Crew", payload.ConversationName);
        Assert.AreEqual("Shift swap anyone?", payload.Preview);
    }

    [TestMethod]
    public async Task Thread_PagesBackThroughOlderMessages()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);

        for (int i = 0; i < 5; i++)
        {
            w.Clock.Advance(TimeSpan.FromMinutes(1));
            await SendAsync(w, w.Amy, direct, $"<p>Message {i}</p>");
        }

        ChatThread newest = (await w.Chat.GetThreadAsync(w.Tom.Id, direct.Id, take: 3)).Data!;
        Assert.IsTrue(newest.HasOlder);
        CollectionAssert.AreEqual(new[] { "Message 2", "Message 3", "Message 4" }, newest.Messages.Select(x => x.Message.BodyText).ToList());

        ChatThread older = (await w.Chat.GetThreadAsync(w.Tom.Id, direct.Id, newest.Messages[0].Message.CreateUtc, take: 3)).Data!;
        Assert.IsFalse(older.HasOlder);
        CollectionAssert.AreEqual(new[] { "Message 0", "Message 1" }, older.Messages.Select(x => x.Message.BodyText).ToList());
    }

    // ---- Unread email and account deletion -------------------------------------------------

    [TestMethod]
    public async Task Digest_EmailsOnceAboutMessagesUnreadForADay()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);

        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await SendAsync(w, w.Tom, direct);

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);

        Assert.AreEqual(0, await ChatDigestHostedService.SweepAsync(factory, w.Notifications, w.Clock.UtcNow.AddHours(23)));
        Assert.AreEqual(1, await ChatDigestHostedService.SweepAsync(factory, w.Notifications, w.Clock.UtcNow.AddHours(25)));
        Assert.AreEqual(0, await ChatDigestHostedService.SweepAsync(factory, w.Notifications, w.Clock.UtcNow.AddHours(26)), "Not twice for the same messages");

        NotificationJob job = w.Notifications.Jobs.Single(x => x.Topic == NotificationTopic.ChatUnreadEmail);
        Assert.AreEqual(w.Amy.Id, job.UserId);
        Assert.AreEqual(w.Amy.Id, job.UserId);
        ChatDigestLine line = ((ChatDigestPayload)job.Payload).Lines.Single();
        Assert.AreEqual(1, line.Count);
        StringAssert.Contains(line.ConversationName, "Tom");
    }

    [TestMethod]
    public async Task Digest_AtMostOnceADayPerPerson_AndPicksUpWhereItLeftOff()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Tom.Id, w.Company.Id, "Crew", [w.Amy.Id])).Data!;
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(w.Options);
        DateTime start = w.Clock.UtcNow;

        // A message an hour, in two conversations, and Amy never opens chat.
        for (int hour = 1; hour <= 30; hour++)
        {
            w.Clock.UtcNow = start.AddHours(hour);
            await SendAsync(w, w.Tom, hour % 2 == 0 ? direct : group, $"<p>Message {hour}</p>");
        }

        int emails = 0;

        // Three days of hourly sweeps: day one covers message 1 (the only one a day old), day two
        // messages 2-25, day three the rest.
        for (int hour = 25; hour <= 80; hour++)
        {
            emails += await ChatDigestHostedService.SweepAsync(factory, w.Notifications, start.AddHours(hour).AddMinutes(30));
        }

        Assert.AreEqual(3, emails, "One a day, not one an hour");

        // Together the emails mention every message exactly once.
        int mentioned = w.Notifications.Jobs
            .Where(x => x.Topic == NotificationTopic.ChatUnreadEmail)
            .Sum(x => ((ChatDigestPayload)x.Payload).Lines.Sum(l => l.Count));
        Assert.AreEqual(30, mentioned);
    }

    [TestMethod]
    public async Task DeletingAnAccount_KeepsGroupMessagesAnonymised_AndErasureEmptiesTheirDirectMessages()
    {
        World w = await SeedAsync();
        ChatConversation direct = await DirectAsync(w, w.Amy, w.Tom);
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id])).Data!;
        ChatMessage dm = await SendAsync(w, w.Tom, direct, "<p>Private</p>");
        ChatMessage post = await SendAsync(w, w.Tom, group, "<p>Team post</p>");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, w.Tom.Id, Now, eraseDirectMessages: true);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChatMessage dmAfter = await ctx.ChatMessages.SingleAsync(x => x.Id == dm.Id);
            ChatMessage postAfter = await ctx.ChatMessages.SingleAsync(x => x.Id == post.Id);

            Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, dmAfter.AuthorUserId);
            Assert.IsTrue(dmAfter.IsDeleted);
            Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, postAfter.AuthorUserId);
            Assert.AreEqual("Team post", postAfter.BodyText);
        }
    }

    [TestMethod]
    public void ChatHtml_TextKeepsLineBreaksAndDecodes()
    {
        ChatHtml.Cleaned cleaned = ChatHtml.Clean("<p>Fish &amp; chips</p><ol><li data-list=\"bullet\">one</li><li data-list=\"bullet\">two</li></ol><p><br></p>")!;

        Assert.AreEqual("Fish & chips\none\ntwo", cleaned.Text);
        Assert.IsFalse(cleaned.Html.EndsWith("<p><br></p>"), "Trailing empty lines are trimmed");
        StringAssert.Contains(cleaned.Html, "data-list=\"bullet\"");
    }
}
