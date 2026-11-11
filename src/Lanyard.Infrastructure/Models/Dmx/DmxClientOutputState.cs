namespace Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// The last universe Lanyard sent a kiosk, plus its grand master and blackout. Written behind
/// (every couple of seconds) by the DMX engine, and read back when the server restarts, so a
/// restart or a kiosk reconnect puts the lights back instead of leaving them dark. One row per
/// kiosk; company-scoped through the Client, like the other kiosk child tables.
/// </summary>
public class DmxClientOutputState
{
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Channel levels as the operator set them (before master/blackout), 512 bytes, channel 1 first.</summary>
    public byte[] Values { get; set; } = new byte[512];

    /// <summary>Scales every dimmable channel: 255 = full, 0 = everything dimmable off.</summary>
    public byte GrandMaster { get; set; } = 255;

    public bool IsBlackout { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
