public interface IPacketSniffer
{
    void StartSniffing();
    Task HandlePacket(string[] decodedData);
}