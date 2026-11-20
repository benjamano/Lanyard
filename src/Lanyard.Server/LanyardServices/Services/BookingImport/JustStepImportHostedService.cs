using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.BookingImport;

// Wakes every so often and imports party bookings for each location that is due one: switched on,
// inside its allowed hours (so it rests overnight), and whose interval has elapsed since its last
// run. The per-location frequency and hours are set on the Booking Import page; the tick here is
// just how often the service checks. Each location is isolated: one bad sign-in logs a failed run
// and raises its own alert, the rest still import. Runs as the system tenant.
public sealed class JustStepImportHostedService(
    ISystemDbContextFactory contextFactory,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<JustStepImportHostedService> logger) : BackgroundService
{
    // How often the service checks for due locations. The real cadence is each location's own
    // frequency (default hourly); this just needs to be fine enough to notice an interval elapsing
    // and an active-hours boundary passing.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

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

    private sealed record Candidate(int CompanyId, int LocationId, int FrequencyHours, int ActiveStartHour, int ActiveEndHour);

    // One pass. Public so tests can run it without the timer. Returns how many locations it imported for.
    public async Task<int> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime venueLocalNow = RotaTime.ToVenueLocal(nowUtc);

        List<(int CompanyId, int LocationId)> targets;

        await using (ApplicationDbContext ctx = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            List<Candidate> candidates = await (
                    from setting in ctx.BookingImportSettings
                    join location in ctx.Locations on setting.LocationId equals location.Id
                    join company in ctx.Companies on location.CompanyId equals company.Id
                    where setting.Enabled && location.IsActive && company.IsActive && !company.IsDemo
                    select new Candidate(location.CompanyId, setting.LocationId, setting.FrequencyHours, setting.ActiveStartHour, setting.ActiveEndHour))
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return 0;
            }

            // The most recent run for each candidate location, to work out whether its interval has elapsed.
            List<int> locationIds = [.. candidates.Select(x => x.LocationId)];
            Dictionary<int, DateTime> lastRun = await ctx.BookingImportRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => locationIds.Contains(x.LocationId))
                .GroupBy(x => x.LocationId)
                .Select(g => new { LocationId = g.Key, Last = g.Max(x => x.StartedAtUtc) })
                .ToDictionaryAsync(x => x.LocationId, x => x.Last, cancellationToken);

            targets =
            [
                .. candidates
                    .Where(c => BookingImportSchedule.IsDue(nowUtc, venueLocalNow,
                        lastRun.TryGetValue(c.LocationId, out DateTime last) ? last : null,
                        c.FrequencyHours, c.ActiveStartHour, c.ActiveEndHour))
                    .Select(c => (c.CompanyId, c.LocationId))
            ];
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
