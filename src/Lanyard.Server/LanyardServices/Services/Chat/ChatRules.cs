using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Chat;

// Rules the chat and moderation services share.
internal static class ChatRules
{
    // The two ids sorted and joined, so either person starting the conversation finds the same one.
    public static string DirectKey(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";

    // Everyone with a membership at any active location of the company.
    public static IQueryable<string> CompanyMemberIds(ApplicationDbContext ctx, int companyId) =>
        ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Location!.CompanyId == companyId && x.Location.IsActive
                && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .Select(x => x.UserId)
            .Distinct();

    public static async Task<bool> InCompanyAsync(ApplicationDbContext ctx, int companyId, IReadOnlyCollection<string> userIds)
    {
        List<string> distinct = userIds.Distinct().ToList();

        if (distinct.Count == 0)
        {
            return true;
        }

        int found = await CompanyMemberIds(ctx, companyId).CountAsync(x => distinct.Contains(x));

        return found == distinct.Count;
    }

    public const string GroupChangeNotAllowed = "Only the person who started this group, or a manager, can rename it or add people.";

    // Renaming a group or adding people is for whoever started it, or a Manager or Admin who's in
    // it. Everyone else can still post, mute and leave.
    public static async Task<bool> CanManageGroupAsync(ApplicationDbContext ctx, string userId, ChatConversation conversation) =>
        conversation.CreatedByUserId == userId
        || await (from userRole in ctx.UserRoles
                  join role in ctx.Roles on userRole.RoleId equals role.Id
                  where userRole.UserId == userId && role.IsActive && (role.Name == "Admin" || role.Name == "Manager")
                  select userRole.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync();

    public static Task<bool> BlockedEitherWayAsync(ApplicationDbContext ctx, string a, string b) =>
        ctx.ChatBlocks.AsNoTracking().TagWithCallSite().AnyAsync(x =>
            (x.BlockerUserId == a && x.BlockedUserId == b) || (x.BlockerUserId == b && x.BlockedUserId == a));

    // Why the person can't post (a suspension in force), or null.
    public static async Task<string?> SuspendedUntilAsync(ApplicationDbContext ctx, string userId, int companyId, DateTime nowUtc)
    {
        ChatSuspension? suspension = await ctx.ChatSuspensions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.CompanyId == companyId && x.LiftedUtc == null && (x.UntilUtc == null || x.UntilUtc > nowUtc))
            .OrderByDescending(x => x.UntilUtc == null)
            .ThenByDescending(x => x.UntilUtc)
            .FirstOrDefaultAsync();

        if (suspension is null)
        {
            return null;
        }

        return suspension.UntilUtc is DateTime until
            ? $"You can't post in chat until {Lanyard.Infrastructure.Enum.RotaTime.ToVenueLocal(until).ToString("ddd d MMM 'at' HH:mm", RotaFormat.Uk)}. You can still read messages."
            : "You can't post in chat until a manager lifts your suspension. You can still read messages.";
    }

    // Deleting a message removes what it said; the row stays so replies and reports still point at
    // something ("Message deleted" / "Removed by a manager").
    public static void Erase(ChatMessage message, string? byUserId, DateTime nowUtc)
    {
        message.DeletedUtc = nowUtc;
        message.DeletedByUserId = byUserId;
        message.BodyHtml = string.Empty;
        message.BodyText = string.Empty;
    }
}
