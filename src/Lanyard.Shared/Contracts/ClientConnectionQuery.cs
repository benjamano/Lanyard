namespace Lanyard.Shared.Contracts;

// Query-string keys a kiosk sends when it opens the /websocket hub connection. The server reads
// them in SignalRControlHub.OnConnectedAsync - both sides use these constants so a renamed key
// can't silently drop the value the way a mismatched string would.
public static class ClientConnectionQuery
{
    public const string ClientId = "clientId";
    public const string Secret = "secret";
    public const string DmxBatch = "dmxBatch";

    // The Velopack version the kiosk is running. Clients older than 1.0.41 don't send it, so the
    // server must treat a missing value as "unknown", not as "no version".
    public const string ClientVersion = "clientVersion";

    // The header form of the shared secret, used by the kiosk's REST calls (the hub can't send
    // headers on every transport, so it uses the Secret query key instead).
    public const string SecretHeader = "X-Lanyard-Client-Secret";
}
