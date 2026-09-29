using Lanyard.Infrastructure.DTO;

public interface IDmxClientService
{
    void SetChannelValue(Guid clientId, int channelAddress, byte value);

    /// <summary>
    /// Records whether a kiosk connection understands the batched DMX message. Kiosks older than
    /// client 1.0.40 only listen for the single-channel message, and they keep running until
    /// their next restart after the server has been redeployed.
    /// </summary>
    void SetConnectionSupportsBatch(string connectionId, bool supportsBatch);

    void ForgetConnection(string connectionId);
}