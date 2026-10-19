using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Policies;

public class PolicyReminderService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    INotificationDispatcher notifications,
    TimeProvider timeProvider,
    ILogger<PolicyReminderService> logger) : IPolicyReminderService
{
    public const int MaxReminders = 3;
    public static readonly TimeSpan OverdueRepeat = TimeSpan.FromDays(7);
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromDays(1);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PolicyReminderService> _logger = logger;

    public async Task<Result<int>> SendDueRemindersAsync(Guid? policyId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            DateTime now = Now;
            DateTime weekAgo = now - OverdueRepeat;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<int> switchedOff = await ctx.CompanyFeatureSettings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Feature == CompanyFeature.Policies && !x.IsEnabled)
                .Select(x => x.CompanyId)
                .ToListAsync(cancellationToken);

            // Due: never reminded for this round, or past the date and not yet told it's overdue
            // (last reminder was before the due date), or overdue and a week since the last one.
            List<PolicyAssignment> due = await ctx.PolicyAssignments
                .Include(x => x.Policy)
                .Where(x => x.IsActive
                    && x.OutstandingFrom != null
                    && x.OutstandingFrom <= now
                    && x.ReminderCount < MaxReminders
                    && x.Policy!.IsActive
                    && !switchedOff.Contains(x.CompanyId)
                    && (policyId == null || x.PolicyId == policyId))
                .Where(x => x.ReminderCount == 0
                    || (x.DueDate != null && x.DueDate < now
                        && (x.LastReminderSentDate == null || x.LastReminderSentDate < x.DueDate || x.LastReminderSentDate <= weekAgo)))
                .ToListAsync(cancellationToken);

            return Result<int>.Ok(await ClaimAndSendAsync(ctx, due, now, countsTowardsLimit: true, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send policy reminders for policy {PolicyId}", policyId);
            return Result<int>.Fail($"Failed to send policy reminders: {ex.Message}");
        }
    }

    public async Task<Result<int>> RemindOutstandingAsync(Guid policyId, LocationScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_tenant.IsSystem || _tenant.CompanyId is not int companyId || (!scope.IsAdmin && !scope.IsManager))
            {
                return Result<int>.Fail("Only Admins and Managers can send reminders.");
            }

            DateTime now = Now;
            DateTime cooldownStart = now - ManualCooldown;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Policy? policy = await ctx.Policies
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == policyId && x.CompanyId == companyId && x.IsActive, cancellationToken);

            if (policy is null || !PolicyService.CanView(policy, scope))
            {
                return Result<int>.Fail("Policy not found.");
            }

            List<string>? visiblePeople = scope.IsAdmin
                ? null
                : await ctx.UserLocationMemberships
                    .Where(x => x.LocationId == scope.LocationId)
                    .Select(x => x.UserId)
                    .ToListAsync(cancellationToken);

            List<PolicyAssignment> outstanding = await ctx.PolicyAssignments
                .Include(x => x.Policy)
                .Where(x => x.PolicyId == policyId
                    && x.IsActive
                    && x.OutstandingFrom != null
                    && x.OutstandingFrom <= now
                    && (x.LastReminderSentDate == null || x.LastReminderSentDate <= cooldownStart)
                    && (visiblePeople == null || visiblePeople.Contains(x.UserId)))
                .ToListAsync(cancellationToken);

            int sent = await ClaimAndSendAsync(ctx, outstanding, now, countsTowardsLimit: false, cancellationToken);

            _logger.LogInformation("Manual reminder for policy {PolicyId} sent to {Count} people", policyId, sent);

            return Result<int>.Ok(sent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reminders for policy {PolicyId}", policyId);
            return Result<int>.Fail($"Failed to send reminders: {ex.Message}");
        }
    }

    // Marks each reminder as sent before queueing it ([ConcurrencyCheck] on LastReminderSentDate),
    // the same claim-then-send order as the staff document and shift reminders: if another run got
    // there first, this save fails and nothing is sent twice.
    private async Task<int> ClaimAndSendAsync(ApplicationDbContext ctx, List<PolicyAssignment> assignments, DateTime now, bool countsTowardsLimit,
        CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return 0;
        }

        Dictionary<string, int> homeLocations = await HomeLocationsAsync(ctx, assignments, cancellationToken);
        int sent = 0;

        foreach (PolicyAssignment assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Policy policy = assignment.Policy!;
            PolicyReminderKind kind = KindFor(assignment, now);

            if (countsTowardsLimit)
            {
                assignment.ReminderCount++;
            }
            else if (assignment.ReminderCount == 0)
            {
                // A manual nudge stands in for the first automatic reminder, so it isn't followed by
                // a second one straight away.
                assignment.ReminderCount = 1;
            }

            assignment.LastReminderSentDate = now;

            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else claimed it (or the person acknowledged it) in the meantime.
                ctx.Entry(assignment).State = EntityState.Detached;
                continue;
            }

            int locationId = policy.LocationId ?? homeLocations.GetValueOrDefault(assignment.UserId);
            string dueText = assignment.DueDate is DateTime dueDate ? RotaTime.ToVenueLocal(dueDate).ToString("d MMM") : "soon";

            _notifications.Enqueue([assignment.UserId], NotificationTopic.PolicyAcknowledgement,
                new PolicyReminderPayload(locationId, policy.Id, policy.Name, policy.CurrentVersionNumber, dueText, kind));
            sent++;
        }

        return sent;
    }

    internal static PolicyReminderKind KindFor(PolicyAssignment assignment, DateTime now)
    {
        if (assignment.DueDate is DateTime due && due < now)
        {
            return PolicyReminderKind.Overdue;
        }

        return assignment.AcknowledgedAtUtc is null ? PolicyReminderKind.New : PolicyReminderKind.ReadAgain;
    }

    // For branding a company-wide policy's email: one of the person's own locations in that company.
    private static async Task<Dictionary<string, int>> HomeLocationsAsync(ApplicationDbContext ctx, List<PolicyAssignment> assignments,
        CancellationToken cancellationToken)
    {
        List<string> userIds = [.. assignments.Where(x => x.Policy!.LocationId is null).Select(x => x.UserId).Distinct()];

        if (userIds.Count == 0)
        {
            return [];
        }

        var memberships = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => userIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.LocationId })
            .ToListAsync(cancellationToken);

        return memberships
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.LocationId));
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;
}
