namespace Lanyard.Client.PacketSniffing;

public interface ILaserGameStatePublisher
{
    void Register();
    Task PublishAsync();
}
