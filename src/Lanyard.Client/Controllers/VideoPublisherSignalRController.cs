using Lanyard.Client.VideoPublisher;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class VideoPublisherSignalRController(IVideoPublisherWindowService windowService, ILogger<VideoPublisherSignalRController> logger)
{
    private readonly IVideoPublisherWindowService _windowService = windowService;
    private readonly ILogger<VideoPublisherSignalRController> _logger = logger;

    public void Register(HubConnection connection)
    {
        connection.On("StartVideoPublisher", () =>
        {
            _logger.LogInformation("Received StartVideoPublisher command from server.");

            _windowService.EnsureRunning();
        });

        connection.On("StopVideoPublisher", () =>
        {
            _logger.LogInformation("Received StopVideoPublisher command from server.");

            _windowService.Stop();
        });
    }
}
