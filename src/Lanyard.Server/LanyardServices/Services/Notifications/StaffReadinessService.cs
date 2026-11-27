using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

public class StaffReadinessService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<StaffReadinessService> logger) : IStaffReadinessService
{
    private static readonly DevicePlatform[] PhonesAndTablets = [DevicePlatform.IPhone, DevicePlatform.IPad, DevicePlatform.Android];

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<StaffReadinessService> _logger = logger;

    public async Task<Result<List<StaffReadinessRow>>> GetAsync(LocationScope scope)
    {
        try
        {
            if (!scope.IsAdmin && !scope.IsManager)
            {
                return Result<List<StaffReadinessRow>>.Fail("Only managers can see who has set up their phone.");
            }

            if (scope.LocationId is not int locationId)
            {
                return Result<List<StaffReadinessRow>>.Fail("Choose a location first.");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Same people as the Users page - everyone with a membership at this location - minus
            // locked accounts, who can't sign in to set anything up (as ChecklistRunService does).
            var people = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(m => m.LocationId == locationId && m.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Where(m => scope.CompanyId == null || m.Location!.CompanyId == scope.CompanyId)
                .Where(m => !(m.User!.LockoutEnabled && m.User.LockoutEnd != null && m.User.LockoutEnd > now))
                .Select(m => new { m.User!.Id, m.User.FirstName, m.User.LastName, m.User.UserName, m.User.PasswordSetDate })
                .Distinct()
                .ToListAsync();

            List<string> ids = people.Select(x => x.Id).ToList();

            var installs = await ctx.AppInstallations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => ids.Contains(x.UserId) && PhonesAndTablets.Contains(x.Platform))
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count(), LastSeen = g.Max(x => x.LastSeenUtc) })
                .ToDictionaryAsync(x => x.UserId);

            // A device whose last send failed has most likely had notifications switched off; it's
            // removed after a few more failures, but shouldn't count as "on" meanwhile.
            Dictionary<string, int> push = await ctx.PushSubscriptions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => ids.Contains(x.UserId) && x.ConsecutiveFailures == 0)
                .GroupBy(x => x.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count);

            HashSet<string> withPasskey = await UsersWithPasskeysAsync(ctx, ids);

            List<StaffReadinessRow> rows = people
                .Select(p => new StaffReadinessRow(
                    p.Id,
                    string.IsNullOrWhiteSpace($"{p.FirstName} {p.LastName}") ? p.UserName ?? p.Id : $"{p.FirstName} {p.LastName}".Trim(),
                    installs.TryGetValue(p.Id, out var i) ? i.Count : 0,
                    push.GetValueOrDefault(p.Id),
                    withPasskey.Contains(p.Id),
                    installs.TryGetValue(p.Id, out var j) ? j.LastSeen : null,
                    p.PasswordSetDate == null))
                .OrderBy(r => r.IsReady)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Result<List<StaffReadinessRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load staff set-up for location {LocationId}", scope.LocationId);
            return Result<List<StaffReadinessRow>>.Fail($"Failed to load who has set up their phone: {ex.Message}");
        }
    }

    // Passkeys live in Identity's own table, which only exists in the model when the context is
    // built with Identity's schema version (always at runtime; not in bare test contexts).
    private static async Task<HashSet<string>> UsersWithPasskeysAsync(ApplicationDbContext ctx, List<string> ids)
    {
        if (ctx.Model.FindEntityType(typeof(IdentityUserPasskey<string>)) is null)
        {
            return [];
        }

        List<string> users = await ctx.Set<IdentityUserPasskey<string>>()
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.UserId))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync();

        return users.ToHashSet();
    }
}
