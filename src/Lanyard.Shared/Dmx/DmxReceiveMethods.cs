namespace Lanyard.Shared.Dmx;

/// <summary>SignalR method names the kiosk listens on for DMX, shared so server and kiosk can't drift.</summary>
public static class DmxReceiveMethods
{
    /// <summary>A whole universe as 512 bytes (channel 1 first). Needs the kiosk's dmxFrame=1.</summary>
    public const string Frame = "ReceiveDmxFrame";
}
