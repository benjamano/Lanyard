using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Tests.Services.OperationsAlerts;

internal static class OperationsAlertTestSupport
{
    // A user with a global role, a member of the given location.
    public static async Task<UserProfile> SeedPersonAsync(DbContextOptions<ApplicationDbContext> options, Location location, string name, string role)
    {
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location, name);

        await using ApplicationDbContext ctx = new(options);

        ApplicationRole? existing = await ctx.Roles.FirstOrDefaultAsync(x => x.Name == role);
        ApplicationRole applicationRole = existing ?? new ApplicationRole
        {
            Id = Guid.NewGuid().ToString(), Name = role, NormalizedName = role.ToUpperInvariant(), CreatedByUserId = "seed", IsActive = true
        };

        if (existing == null)
        {
            ctx.Roles.Add(applicationRole);
        }

        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = applicationRole.Id });
        await ctx.SaveChangesAsync();

        return user;
    }

    public static async Task<OperationsAlert> SeedAlertAsync(DbContextOptions<ApplicationDbContext> options, int companyId, int? locationId,
        string key, OperationsAlertStatus status = OperationsAlertStatus.Open, OperationsAlertSeverity severity = OperationsAlertSeverity.Warning,
        DateTime? raisedAt = null, DateTime? resolvedAt = null, string? subjectUserId = null)
    {
        await using ApplicationDbContext ctx = new(options);

        OperationsAlert alert = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            LocationId = locationId,
            Type = OperationsAlertType.KioskOffline,
            Severity = severity,
            Status = status,
            DedupKey = key,
            Title = $"Alert {key}",
            RaisedAtUtc = raisedAt ?? new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc),
            LastSeenAtUtc = raisedAt ?? new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc),
            ResolvedAtUtc = resolvedAt,
            Resolution = status == OperationsAlertStatus.Resolved ? OperationsAlertResolution.Auto : null,
            SubjectUserId = subjectUserId
        };

        ctx.OperationsAlerts.Add(alert);
        await ctx.SaveChangesAsync();

        return alert;
    }
}
