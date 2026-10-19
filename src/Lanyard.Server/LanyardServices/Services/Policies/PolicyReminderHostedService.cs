using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Policies;

/// <summary>
/// Every hour: brings each company's policy audiences up to date (people who joined or left a
/// location or position since), then sends the policy reminders that are due. Reminders only go
/// out between 08:00 and 20:00 UK time, so nobody is woken up about a policy; anything due
/// overnight goes in the first morning sweep. Demo and inactive companies are skipped.
/// </summary>
public class PolicyReminderHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PolicyReminderHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    public static readonly TimeOnly SendFrom = new(8, 0);
    public static readonly TimeOnly SendUntil = new(20, 0);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PolicyReminderHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PolicyReminderHostedService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    await RunSweepAsync(
                        scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>(),
                        scope.ServiceProvider.GetRequiredService<IPolicyAudienceService>(),
                        scope.ServiceProvider.GetRequiredService<IPolicyReminderService>(),
                        stoppingToken);
                }

                await Task.Delay(SweepInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PolicyReminderHostedService terminated with an unhandled exception");
        }

        _logger.LogInformation("PolicyReminderHostedService stopped");
    }

    // Public so tests can run one sweep without the timer. Returns how many reminders were queued.
    public async Task<int> RunSweepAsync(IDbContextFactory<ApplicationDbContext> factory, IPolicyAudienceService audience,
        IPolicyReminderService reminders, CancellationToken cancellationToken = default)
    {
        List<int> companyIds;

        await using (ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken))
        {
            List<int> active = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && !x.IsDemo && ctx.Policies.Any(p => p.CompanyId == x.Id && p.IsActive))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            HashSet<int> switchedOff = [.. await ctx.CompanyFeatureSettings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Feature == CompanyFeature.Policies && !x.IsEnabled)
                .Select(x => x.CompanyId)
                .ToListAsync(cancellationToken)];

            companyIds = [.. active.Where(x => !switchedOff.Contains(x))];
        }

        foreach (int companyId in companyIds)
        {
            Result<PolicySyncResult> sync = await audience.SyncAsync(companyId, null, cancellationToken);

            if (!sync.IsSuccess)
            {
                _logger.LogWarning("Policy audience sync failed for company {CompanyId}: {Error}", companyId, sync.Error);
            }
        }

        TimeOnly venueTime = RotaTime.LocalTime(_timeProvider.GetUtcNow().UtcDateTime);

        if (companyIds.Count == 0 || venueTime < SendFrom || venueTime >= SendUntil)
        {
            return 0;
        }

        Result<int> sent = await reminders.SendDueRemindersAsync(null, cancellationToken);

        if (!sent.IsSuccess)
        {
            _logger.LogWarning("Policy reminders couldn't be sent: {Error}", sent.Error);
            return 0;
        }

        if (sent.Data > 0)
        {
            _logger.LogInformation("Queued {Count} policy reminders", sent.Data);
        }

        return sent.Data;
    }
}
