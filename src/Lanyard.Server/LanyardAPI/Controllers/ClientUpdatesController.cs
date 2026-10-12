using Lanyard.API.Extensions;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.ClientReleases;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers;

// Kiosk-facing: the client calls this on startup (and when an admin presses "Update now") to find
// out which version it should be on. Authenticated with the kiosk shared secret like the other
// kiosk endpoints. Read-only - nothing a kiosk sends here can change its own update setting.
[ApiController]
public class ClientUpdatesController(IClientUpdatePolicyService updatePolicyService, IClientSecretValidator clientSecretValidator) : ControllerBase
{
    private readonly IClientUpdatePolicyService _updatePolicyService = updatePolicyService;
    private readonly IClientSecretValidator _clientSecretValidator = clientSecretValidator;

    [HttpGet(ClientUpdateRoutes.FeedTemplate)]
    public async Task<IActionResult> GetFeed(Guid clientId, CancellationToken cancellationToken)
    {
        if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
        {
            return Unauthorized();
        }

        Result<ClientUpdateFeedResponse> result = await _updatePolicyService.GetFeedForClientAsync(clientId, cancellationToken);

        return result.ToActionResult();
    }
}
