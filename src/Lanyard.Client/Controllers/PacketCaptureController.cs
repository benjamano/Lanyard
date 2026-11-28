using Lanyard.Client.GameRecordings;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

/// <summary>
/// The server's recording and replay commands. Each is a client result: the server waits for the
/// kiosk to say whether it started, and a kiosk older than 1.0.50 has no handler, which the
/// server sees as a HubException. Only the server can call these: they arrive on the kiosk's own
/// authenticated hub connection.
/// </summary>
public class PacketCaptureController(IPacketCaptureCoordinator coordinator, ILogger<PacketCaptureController> logger)
{
    public void Register(HubConnection connection)
    {
        connection.On<StartPacketRecordingRequestDTO, PacketCaptureCommandResultDTO>(ClientHubMethods.StartPacketRecording, async request =>
        {
            request ??= new StartPacketRecordingRequestDTO();

            logger.LogInformation("The server asked to start recording laser game packets ({Mode}, up to {Minutes} min).", request.Mode, request.MaxMinutes);

            return await coordinator.StartRecordingAsync(request);
        });

        connection.On<PacketCaptureCommandResultDTO>(ClientHubMethods.StopPacketRecording, async () =>
        {
            logger.LogInformation("The server asked to stop recording laser game packets.");

            return await coordinator.StopRecordingAsync();
        });

        connection.On<StartPacketReplayRequestDTO, PacketCaptureCommandResultDTO>(ClientHubMethods.StartPacketReplay, async request =>
        {
            if (request == null)
            {
                return PacketCaptureCommandResultDTO.Refused("No recording was chosen.", coordinator.GetState());
            }

            logger.LogInformation("The server asked to replay recording {RecordingId} at {Speed}x.", request.RecordingId, request.Speed);

            return await coordinator.StartReplayAsync(request, CancellationToken.None);
        });

        connection.On<PacketCaptureCommandResultDTO>(ClientHubMethods.StopPacketReplay, async () =>
        {
            logger.LogInformation("The server asked to stop the replay.");

            return await coordinator.StopReplayAsync();
        });
    }
}
