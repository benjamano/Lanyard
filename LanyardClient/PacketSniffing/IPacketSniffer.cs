public interface IPacketSniffer
{
    void StartSniffing();
    void HandlePacket(string[] decodedData);
}