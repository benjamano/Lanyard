using Lanyard.Shared.Enum;

namespace Lanyard.Client.RemoteRestart;

public interface IRemoteRestartService
{
    /// <summary>
    /// Accepts a restart request and carries it out shortly afterwards, so the caller can
    /// acknowledge it to the server first. Returns false if this client can't honour it.
    /// </summary>
    bool Restart(ClientRestartType restartType);
}
