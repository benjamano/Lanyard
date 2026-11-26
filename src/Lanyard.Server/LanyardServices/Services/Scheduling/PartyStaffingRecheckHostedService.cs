using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// Every 15 minutes, clears "Party staffing out of date" alerts whose day is back in line (a manager
// fixed the rota by hand) or has passed. Rota edits aren't hooked one by one; this catches them all.
// It never raises anything new (see IPartyStaffingWatch.RecheckOpenAsync).
public sealed class PartyStaffingRecheckHostedService(IPartyStaffingWatch watch, ILogger<PartyStaffingRecheckHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PartyStaffingRecheckHostedService started");

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            using PeriodicTimer timer = new(Interval);

            do
            {
                await watch.RecheckOpenAsync(null, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("PartyStaffingRecheckHostedService stopped");
    }
}
