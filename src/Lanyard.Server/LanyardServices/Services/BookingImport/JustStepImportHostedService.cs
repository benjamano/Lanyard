using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.BookingImport;

// Every five minutes, imports party bookings for each location that has it switched on, across all
// active non-demo companies. Each location is isolated: one bad sign-in logs a failed run and raises
// its own alert, the rest still import. Runs as the system tenant and resolves the scoped import
// service per tick.
public sealed class JustStepImportHostedService(
    ISystemDbContextFactory contextFactory,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<JustStepImportHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // Let the app finish starting before reaching out to an external site.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly ISystemDbContextFactory _contextFactory = contextFactory;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<JustStepImportHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JustStepImportHostedService started");

        try
        {
            await Task.Delay(StartupDelay, _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(SweepInterval, _timeProvider);

            do
            {
                try
                {
                    await RunSweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Booking import sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("JustStepImportHostedService stopped");
    }

    // One pass. Public so tests can run it without the timer. Returns how many locations it imported for.
    public async Task<int> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        List<(int CompanyId, int LocationId)> targets;

        await using (ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var rows = await (
                    from setting in ctx.BookingImportSettings
                    join location in ctx.Locations on setting.LocationId equals location.Id
                    join company in ctx.Companies on location.CompanyId equals company.Id
                    where setting.Enabled && location.IsActive && company.IsActive && !company.IsDemo
                    select new { location.CompanyId, setting.LocationId })
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(cancellationToken);

            targets = [.. rows.Select(x => (x.CompanyId, x.LocationId))];
        }

        if (targets.Count == 0)
        {
            return 0;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IBookingImportService importService = scope.ServiceProvider.GetRequiredService<IBookingImportService>();

        int ran = 0;

        foreach ((int companyId, int locationId) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await importService.RunImportAsync(companyId, locationId, BookingImportTrigger.Scheduled, cancellationToken);
                ran++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RunImportAsync records its own failed run and alert; this is only a last resort.
                _logger.LogError(ex, "Booking import threw for location {LocationId}", locationId);
            }
        }

        return ran;
    }
}
