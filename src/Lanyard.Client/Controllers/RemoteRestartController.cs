using Lanyard.Client.RemoteRestart;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class RemoteRestartController(IRemoteRestartService remoteRestartService, ILogger<RemoteRestartController> logger)
{
    private readonly IRemoteRestartService _remoteRestartService = remoteRestartService;
    private readonly ILogger<RemoteRestartController> _logger = logger;

    public void Register(HubConnection connection)
    {
        // A client result, not a plain On(): the server awaits this return value, which is how
        // it tells a kiosk that accepted the restart apart from one too old to have this handler.
        connection.On<ClientRestartType, bool>("RestartClient", restartType =>
        {
            _logger.LogInformation("Received remote restart command: {RestartType}", restartType);

            return _remoteRestartService.Restart(restartType);
        });
    }
}
