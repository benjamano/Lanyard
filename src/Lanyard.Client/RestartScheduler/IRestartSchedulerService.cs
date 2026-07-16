using Lanyard.Infrastructure.DTO;

namespace Lanyard.Client.RestartScheduler;

public interface IRestartSchedulerService
{
    void Apply(ClientRestartScheduleDTO schedule);
}
