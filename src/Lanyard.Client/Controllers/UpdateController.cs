using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using AutoUpdater = Lanyard.Client.AutoUpdate.AutoUpdate;

namespace Lanyard.Client.Controllers;

public class UpdateController(ILogger<UpdateController> logger)
{
    private readonly ILogger<UpdateController> _logger = logger;

    public void Register(HubConnection connection)
    {
        // A client result, so the server can tell "updating", "already up to date" and "this
        // kiosk is too old to do this on request" (no handler, so the invoke fails) apart.
        connection.On<bool>(ClientHubEvents.CheckForUpdates, async () =>
        {
            _logger.LogInformation("Server asked this client to check for updates");

            if (!Guid.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID"), out Guid clientId))
            {
                _logger.LogWarning("Can't check for updates: LANYARD_CLIENT_ID isn't set");
                return false;
            }

            bool updating = await AutoUpdater.CheckOnRequestAsync(clientId);

            _logger.LogInformation("Update check on request: {Outcome}", updating ? "an update is downloading" : "already on the target version");

            return updating;
        });
    }
}
