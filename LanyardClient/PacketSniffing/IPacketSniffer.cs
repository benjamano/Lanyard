public interface IPacketSniffer
{
    Task StartSniffingAsync();
    Task HandlePacketAsync(string[] decodedData);
}