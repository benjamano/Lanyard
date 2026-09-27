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
        connection.On<ClientRestartType>("RestartClient", restartType =>
        {
            _logger.LogInformation("Received remote restart command: {RestartType}", restartType);

            _remoteRestartService.Restart(restartType);
        });
    }
}
