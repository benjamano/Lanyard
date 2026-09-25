using Lanyard.Application.Services.Chat;
using Lanyard.Application.Services.Locations;
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

// Location and company channels: who's in them (whoever works there), who can post, pin and
// remove, pinned posts, and the open-shift and swap cards the rota posts into them.
[TestClass]
public class ChatChannelTests
{
    private static readonly DateTime Now = DateTime.UtcNow.Date.AddHours(10);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Location,
        Location Other,
        UserProfile Manager,
        UserProfile Admin,
        UserProfile Amy,
        UserProfile Tom,
        RecordingNotificationDispatcher Notifications,
        ChatService Chat,
        ChatModerationService Moderation)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Location);
        public LocationScope AdminScope => new(true, Location.Id, Company.Id, Location.Name, true);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location other;

        await using (ApplicationDbContext ctx = new(options))
        {
            other = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(other);
            await ctx.SaveChangesAsync();
        }

        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        UserProfile admin = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ada");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");

        await using (ApplicationDbContext ctx = new(options))
        {
            ApplicationRole managerRole = new() { Id = Guid.NewGuid().ToString(), Name = "Manager", NormalizedName = "MANAGER", CreatedByUserId = "seed", IsActive = true };
            ApplicationRole adminRole = new() { Id = Guid.NewGuid().ToString(), Name = "Admin", NormalizedName = "ADMIN", CreatedByUserId = "seed", IsActive = true };
            ctx.Roles.AddRange(managerRole, adminRole);
            ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = manager.Id, RoleId = managerRole.Id });
            ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = adminRole.Id });
            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(Now);
        RecordingNotificationDispatcher notifications = new();
        ChatEventBus bus = new();
        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);

        return new World(options, company, location, other, manager, admin, amy, tom, notifications,
            new ChatService(factory, notifications, bus, new ChatPresence(), clock, NullLogger<ChatService>.Instance),
            new ChatModerationService(factory, notifications, bus, clock, NullLogger<ChatModerationService>.Instance));
    }

    private static async Task<List<ChatInboxItem>> InboxAsync(World w, UserProfile user)
    {
        Result<List<ChatInboxItem>> inbox = await w.Chat.GetInboxAsync(user.Id);
        Assert.IsTrue(inbox.IsSuccess, inbox.Error);
        return inbox.Data!;
    }

    private static async Task<ChatInboxItem> ChannelAsync(World w, UserProfile user, ChatConversationKind kind) =>
        (await InboxAsync(w, user)).Single(x => x.Kind == kind);

    [TestMethod]
    public async Task OpeningChat_JoinsYourLocationAndCompanyChannels()
    {
        World w = await SeedAsync();

        List<ChatInboxItem> inbox = await InboxAsync(w, w.Amy);

        Assert.AreEqual("Ipswich team", inbox.Single(x => x.Kind == ChatConversationKind.LocationChannel).Title);
        Assert.AreEqual("Play2Day everyone", inbox.Single(x => x.Kind == ChatConversationKind.CompanyChannel).Title);

        // Opening it again reuses the same channels.
        List<ChatInboxItem> again = (await w.Chat.GetInboxAsync(w.Tom.Id)).Data!;
        Assert.AreEqual(inbox.Single(x => x.Kind == ChatConversationKind.LocationChannel).ConversationId,
            again.Single(x => x.Kind == ChatConversationKind.LocationChannel).ConversationId);
    }

    [TestMethod]
    public async Task LeavingALocation_LeavesItsChannel()
    {
        World w = await SeedAsync();
        await w.Chat.GetInboxAsync(w.Amy.Id);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            UserLocationMembership membership = await ctx.UserLocationMemberships.SingleAsync(x => x.UserId == w.Amy.Id);
            membership.LocationId = w.Other.Id;
            await ctx.SaveChangesAsync();
        }

        List<ChatInboxItem> inbox = await InboxAsync(w, w.Amy);

        Assert.AreEqual("Wisbech team", inbox.Single(x => x.Kind == ChatConversationKind.LocationChannel).Title);
    }

    [TestMethod]
    public async Task NewStarters_CanReadTheChannelsHistory()
    {
        World w = await SeedAsync();
        ChatInboxItem channel = await ChannelAsync(w, w.Amy, ChatConversationKind.LocationChannel);
        await w.Chat.SendAsync(w.Amy.Id, channel.ConversationId, "<p>Welcome, everyone</p>");

        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Location, "Nia");
        await w.Chat.GetInboxAsync(newStarter.Id);

        ChatThread thread = (await w.Chat.GetThreadAsync(newStarter.Id, channel.ConversationId)).Data!;

        Assert.AreEqual("Welcome, everyone", thread.Messages.Single().Message.BodyText);
        Assert.AreEqual(0, (await w.Chat.GetUnreadTotalAsync(newStarter.Id)).Data, "Joining starts with the channel read");
    }

    [TestMethod]
    public async Task PostingSwitch_LetsOnlyManagersPostWhenOff()
    {
        World w = await SeedAsync();
        ChatInboxItem channel = await ChannelAsync(w, w.Amy, ChatConversationKind.LocationChannel);
        await w.Chat.GetInboxAsync(w.Manager.Id);

        Assert.IsTrue((await w.Moderation.SetStaffCanPostAsync(w.ManagerScope, channel.ConversationId, false, w.Manager.Id)).IsSuccess);

        Result<ChatMessage> staff = await w.Chat.SendAsync(w.Amy.Id, channel.ConversationId, "<p>Hi</p>");
        Result<ChatMessage> manager = await w.Chat.SendAsync(w.Manager.Id, channel.ConversationId, "<p>Hi team</p>");

        StringAssert.Contains(staff.Error, "Only managers can post");
        Assert.IsTrue(manager.IsSuccess, manager.Error);
    }

    [TestMethod]
    public async Task CompanyChannel_IsTheAdminsToLookAfter()
    {
        World w = await SeedAsync();
        ChatInboxItem company = await ChannelAsync(w, w.Amy, ChatConversationKind.CompanyChannel);

        Assert.IsFalse((await w.Moderation.SetStaffCanPostAsync(w.ManagerScope, company.ConversationId, false, w.Manager.Id)).IsSuccess);
        Assert.IsTrue((await w.Moderation.SetStaffCanPostAsync(w.AdminScope, company.ConversationId, false, w.Admin.Id)).IsSuccess);

        List<ChatChannelAdminView> managers = (await w.Moderation.GetChannelsAsync(w.ManagerScope)).Data!;
        Assert.IsTrue(managers.All(x => x.Channel.Kind == ChatConversationKind.LocationChannel && x.Channel.LocationId == w.Location.Id));

        List<ChatChannelAdminView> admins = (await w.Moderation.GetChannelsAsync(w.AdminScope)).Data!;
        Assert.AreEqual(3, admins.Count, "Both locations and the company");
    }

    [TestMethod]
    public async Task ManageChannels_CountsEveryoneWhoWorksThere_BeforeAnyoneOpensChat()
    {
        World w = await SeedAsync();

        List<ChatChannelAdminView> channels = (await w.Moderation.GetChannelsAsync(w.ManagerScope)).Data!;

        Assert.AreEqual(4, channels.Single().MemberCount);
    }

    [TestMethod]
    public async Task Pinning_IsForManagers_AndTellsEveryoneElseInTheChannel()
    {
        World w = await SeedAsync();
        ChatInboxItem channel = await ChannelAsync(w, w.Amy, ChatConversationKind.LocationChannel);
        ChatMessage message = (await w.Chat.SendAsync(w.Amy.Id, channel.ConversationId, "<p>Lost property box is full</p>")).Data!;

        Assert.IsFalse((await w.Chat.SetPinnedAsync(w.Tom.Id, message.Id, true)).IsSuccess);

        // Tom has never opened chat, but he still hears about it.
        Assert.IsTrue((await w.Chat.SetPinnedAsync(w.Manager.Id, message.Id, true)).IsSuccess);

        List<string> told = w.Notifications.Jobs.Where(x => x.Topic == NotificationTopic.PinnedPost).Select(x => x.UserId).ToList();
        CollectionAssert.Contains(told, w.Tom.Id);
        CollectionAssert.Contains(told, w.Amy.Id);
        CollectionAssert.DoesNotContain(told, w.Manager.Id);

        ChatThread thread = (await w.Chat.GetThreadAsync(w.Amy.Id, channel.ConversationId)).Data!;
        Assert.AreEqual(message.Id, thread.Pinned.Single().Message.Id);
        Assert.AreEqual(message.Id, (await w.Chat.GetPinnedForUserAsync(w.Tom.Id)).Data!.Single().Message.Message.Id);
    }

    [TestMethod]
    public async Task PinnedPost_WrittenByAManager_ForTheirOwnLocationOnly()
    {
        World w = await SeedAsync();
        await w.Chat.GetInboxAsync(w.Amy.Id);
        List<ChatChannelAdminView> admins = (await w.Moderation.GetChannelsAsync(w.AdminScope)).Data!;
        ChatConversation mine = admins.Single(x => x.Channel.LocationId == w.Location.Id).Channel;
        ChatConversation otherLocation = admins.Single(x => x.Channel.LocationId == w.Other.Id).Channel;

        Assert.IsFalse((await w.Moderation.PostPinnedAsync(w.ManagerScope, otherLocation.Id, "<p>Hi</p>", w.Manager.Id)).IsSuccess);

        Result<ChatMessage> posted = await w.Moderation.PostPinnedAsync(w.ManagerScope, mine.Id, "<p>Staff meeting Monday 9am</p>", w.Manager.Id);

        Assert.IsTrue(posted.IsSuccess, posted.Error);
        Assert.IsTrue(posted.Data!.IsPinned);
        CollectionAssert.Contains(w.Notifications.Jobs.Where(x => x.Topic == NotificationTopic.PinnedPost).Select(x => x.UserId).ToList(), w.Amy.Id);
    }

    [TestMethod]
    public async Task Removing_IsForManagers_AndListedForThem()
    {
        World w = await SeedAsync();
        ChatInboxItem channel = await ChannelAsync(w, w.Amy, ChatConversationKind.LocationChannel);
        ChatMessage message = (await w.Chat.SendAsync(w.Amy.Id, channel.ConversationId, "<p>Something rude</p>")).Data!;

        Assert.IsFalse((await w.Chat.RemoveAsModeratorAsync(w.Tom.Id, message.Id)).IsSuccess);
        Assert.IsTrue((await w.Chat.RemoveAsModeratorAsync(w.Manager.Id, message.Id)).IsSuccess);

        ChatMessageView removed = (await w.Chat.GetThreadAsync(w.Tom.Id, channel.ConversationId)).Data!.Messages.Single();
        Assert.IsTrue(removed.Message.IsDeleted);
        Assert.AreEqual(w.Manager.Id, removed.Message.DeletedByUserId);

        ChatRemovalView listed = (await w.Moderation.GetRecentRemovalsAsync(w.ManagerScope)).Data!.Single();
        Assert.AreEqual("Ipswich team", listed.ChannelName);
    }

    [TestMethod]
    public async Task RemovingFromAGroup_IsntAChannelModeratorsJob()
    {
        World w = await SeedAsync();
        ChatConversation group = (await w.Chat.CreateGroupAsync(w.Amy.Id, w.Company.Id, "Crew", [w.Tom.Id, w.Manager.Id])).Data!;
        ChatMessage message = (await w.Chat.SendAsync(w.Amy.Id, group.Id, "<p>Hi</p>")).Data!;

        Assert.IsFalse((await w.Chat.RemoveAsModeratorAsync(w.Manager.Id, message.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task ChannelMessages_UseTheirOwnNotificationTopic()
    {
        World w = await SeedAsync();
        ChatInboxItem channel = await ChannelAsync(w, w.Amy, ChatConversationKind.LocationChannel);

        await w.Chat.SendAsync(w.Amy.Id, channel.ConversationId, "<p>Anyone seen the till keys?</p>");

        List<NotificationJob> jobs = w.Notifications.Jobs.Where(x => x.Topic == NotificationTopic.ChannelMessage).ToList();
        CollectionAssert.AreEquivalent(new[] { w.Manager.Id, w.Admin.Id, w.Tom.Id }, jobs.Select(x => x.UserId).ToList());
    }

    [TestMethod]
    public async Task OpenShiftCard_PostedWhenAShiftIsOpenedUp_AndShowsLiveStatus()
    {
        World w = await SeedAsync();

        Shift shift;
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            shift = new Shift
            {
                Id = Guid.NewGuid(), LocationId = w.Location.Id, UserId = w.Amy.Id,
                StartUtc = Now.AddDays(5), EndUtc = Now.AddDays(5).AddHours(6), IsActive = true,
                CreateDate = Now.AddDays(-1), CreateByUserId = w.Manager.Id, PublishedDateUtc = Now.AddDays(-1), PublishedStartUtc = Now.AddDays(5)
            };
            ctx.Shifts.Add(shift);
            await ctx.SaveChangesAsync();
        }

        ShiftClaimService claims = new(SchedulingTestHelpers.GetFactory(w.Options), w.Notifications, new ShiftClaimEventBus(),
            new TestClock(Now), NullLogger<ShiftClaimService>.Instance);

        Result<bool> released = await claims.ReleaseShiftAsync(w.ManagerScope, shift.Id, w.Manager.Id);
        Assert.IsTrue(released.IsSuccess, released.Error);

        ChatInboxItem channel = await ChannelAsync(w, w.Tom, ChatConversationKind.LocationChannel);
        ChatMessageView card = (await w.Chat.GetThreadAsync(w.Tom.Id, channel.ConversationId)).Data!.Messages.Single();

        Assert.AreEqual(ChatMessageKind.OpenShiftCard, card.Message.Kind);
        Assert.IsTrue(card.Card!.IsOpen);
        Assert.AreEqual("/rota/open", card.Card.ActionUrl);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.Shifts.SingleAsync(x => x.Id == shift.Id)).UserId = w.Tom.Id;
            await ctx.SaveChangesAsync();
        }

        ChatCardView after = (await w.Chat.GetThreadAsync(w.Tom.Id, channel.ConversationId)).Data!.Messages.Single().Card!;
        Assert.IsFalse(after.IsOpen);
        StringAssert.Contains(after.Status, "Covered by Tom");
    }

    [TestMethod]
    public async Task SwapCard_PostedWhenSomeoneAsksForASwap()
    {
        World w = await SeedAsync();

        Shift shift;
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            shift = new Shift
            {
                Id = Guid.NewGuid(), LocationId = w.Location.Id, UserId = w.Amy.Id,
                StartUtc = Now.AddDays(5), EndUtc = Now.AddDays(5).AddHours(6), IsActive = true,
                CreateDate = Now.AddDays(-1), CreateByUserId = w.Manager.Id, PublishedDateUtc = Now.AddDays(-1), PublishedStartUtc = Now.AddDays(5)
            };
            ctx.Shifts.Add(shift);
            await ctx.SaveChangesAsync();
        }

        ShiftClaimService claims = new(SchedulingTestHelpers.GetFactory(w.Options), w.Notifications, new ShiftClaimEventBus(),
            new TestClock(Now), NullLogger<ShiftClaimService>.Instance);

        await claims.RequestSwapAsync(w.Amy.Id, shift.Id, "Wedding");

        ChatInboxItem channel = await ChannelAsync(w, w.Tom, ChatConversationKind.LocationChannel);
        ChatMessageView card = (await w.Chat.GetThreadAsync(w.Tom.Id, channel.ConversationId)).Data!.Messages.Single();

        Assert.AreEqual(ChatMessageKind.SwapRequestCard, card.Message.Kind);
        Assert.AreEqual("Looking for offers", card.Card!.Status);
        StringAssert.Contains(card.Card.Title, "Amy");
    }
}
