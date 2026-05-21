using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;

namespace Lanyard.Client.SignalR;

public interface ISignalRClient
{
    Task Connect(List<Action<HubConnection>> registrations);
    Task SendLaserGameStatusAsync(LaserGameStatusDTO status);
    Task SendDmxChannelValueAsync(int channel, byte value);
}
