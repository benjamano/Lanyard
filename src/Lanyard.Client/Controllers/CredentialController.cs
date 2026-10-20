using Lanyard.Client.Security;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

// An Admin rotated this kiosk's credential: save the new one and say so. The server only retires
// the old credential after this returns true, then closes the connection so the kiosk reconnects
// on the new one - so a failure here leaves the kiosk exactly as it was.
public class CredentialController(ILogger<CredentialController> logger)
{
    private readonly ILogger<CredentialController> _logger = logger;

    public void Register(HubConnection connection)
    {
        connection.On<string, bool>(ClientHubEvents.ReceiveCredential, credential =>
        {
            if (string.IsNullOrWhiteSpace(credential))
            {
                _logger.LogWarning("The server sent an empty credential; keeping the current one");
                return false;
            }

            try
            {
                KioskCredentials.Replace(credential);
                KioskMediaTokens.Clear();

                _logger.LogInformation("Saved this kiosk's new credential (rotated by an Admin)");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Couldn't save the new credential; keeping the current one");
                return false;
            }
        });
    }
}
