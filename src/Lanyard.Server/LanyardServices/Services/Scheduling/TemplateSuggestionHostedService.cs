using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// Once a month, for every location that uses shift templates, compares the rota it published over
// the last few weeks with its templates (TemplateLearningService.RunMonthlyAsync) and records how
// many changes that suggests, for a banner on Shift Templates. Checks daily and runs a location
// once its last run is from an earlier calendar month (venue time), so a restart never skips or
// repeats a month.
public class TemplateSuggestionHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TemplateSuggestionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<TemplateSuggestionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TemplateSuggestionHostedService started");

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(Interval, _timeProvider);

            do
            {
                await RunDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("TemplateSuggestionHostedService stopped");
    }

    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            ISystemDbContextFactory factory = scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>();
            TemplateLearningService learning = (TemplateLearningService)scope.ServiceProvider.GetRequiredService<ITemplateLearningService>();

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            DateOnly today = RotaTime.Today(nowUtc);
            DateOnly monthStart = new(today.Year, today.Month, 1);
            DateTime monthStartUtc = RotaTime.StartOfDayUtc(monthStart);

            List<int> withTemplates = await ctx.ShiftTemplates
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive)
                .Select(x => x.LocationId)
                .Distinct()
                .ToListAsync(cancellationToken);

            List<Location> locations = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && withTemplates.Contains(x.Id))
                .ToListAsync(cancellationToken);

            foreach (Location location in locations)
            {
                bool ranThisMonth = await ctx.TemplateSuggestionRuns
                    .AsNoTracking()
                    .AnyAsync(x => x.LocationId == location.Id && x.RunUtc >= monthStartUtc, cancellationToken);

                if (ranThisMonth || !await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, location.CompanyId, CompanyFeature.StaffScheduling, cancellationToken))
                {
                    continue;
                }

                try
                {
                    TemplateSuggestionRun? run = await learning.RunMonthlyAsync(ctx, location, nowUtc);

                    if (run is not null)
                    {
                        _logger.LogInformation("Monthly template suggestions for {LocationId}: {Suggestions}", location.Id, run.Suggestions);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Monthly template suggestions failed for {LocationId}", location.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running monthly template suggestions");
        }
    }
}
