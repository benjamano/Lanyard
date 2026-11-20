using Lanyard.Client.Identify;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class DisplayIdentifyController(IDisplayIdentifyService identifyService, ILogger<DisplayIdentifyController> logger)
{
    private readonly IDisplayIdentifyService _identifyService = identifyService;
    private readonly ILogger<DisplayIdentifyController> _logger = logger;

    public void Register(HubConnection connection)
    {
        // A client result, like the self-test: the server waits for the screen list, and a kiosk
        // too old to have this handler comes back to it as a HubException.
        connection.On<DisplayIdentifyRequestDTO, DisplayIdentifyResultDTO>(ClientHubMethods.IdentifyDisplays, async request =>
        {
            request ??= new DisplayIdentifyRequestDTO();

            _logger.LogInformation("Identifying displays for the server (request {RequestId})", request.RequestId);

            List<IdentifiedDisplayDTO> displays = await _identifyService.ShowAsync(request);

            return new DisplayIdentifyResultDTO
            {
                RequestId = request.RequestId,
                ClientVersion = ClientDiagnosticsController.GetClientVersion(),
                Displays = displays
            };
        });
    }
}
