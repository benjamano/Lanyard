using Lanyard.Infrastructure.DTO;

// The kiosk-facing side of the DMX engine, used by SignalRControlHub.
public interface IDmxClientService
{
    void SetChannelValue(Guid clientId, int channelAddress, byte value);

    /// <summary>
    /// Records whether a kiosk connection understands the batched DMX message. Kiosks older than
    /// client 1.0.40 only listen for the single-channel message, and they keep running until
    /// their next restart after the server has been redeployed.
    /// </summary>
    void SetConnectionSupportsBatch(string connectionId, bool supportsBatch);

    /// <summary>
    /// Tells the engine which connection now reaches a kiosk (so sends need no database lookup),
    /// and re-sends the universe the server holds for it, so a kiosk that restarted to an all-zero
    /// frame gets its lights back.
    /// </summary>
    Task RegisterConnectionAsync(Guid clientId, string connectionId);

    void ForgetConnection(string connectionId);
}
