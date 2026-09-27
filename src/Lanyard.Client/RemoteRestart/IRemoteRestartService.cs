using Lanyard.Shared.Enum;

namespace Lanyard.Client.RemoteRestart;

public interface IRemoteRestartService
{
    void Restart(ClientRestartType restartType);
}
