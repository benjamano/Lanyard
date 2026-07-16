using Lanyard.Client.RestartScheduler;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class RestartScheduleController(IRestartSchedulerService restartSchedulerService, ILogger<RestartScheduleController> logger)
{
    private readonly IRestartSchedulerService _restartSchedulerService = restartSchedulerService;
    private readonly ILogger<RestartScheduleController> _logger = logger;

    public void Register(HubConnection connection)
    {
        connection.On<ClientRestartScheduleDTO>("ReceiveRestartSchedule", schedule =>
        {
            _logger.LogInformation("Received restart schedule: enabled {Enabled}, every {IntervalCount} {IntervalUnit} at {TimeOfDay}", schedule.Enabled, schedule.IntervalCount, schedule.IntervalUnit, schedule.TimeOfDay);

            _restartSchedulerService.Apply(schedule);
        });
    }
}
