namespace Lanyard.Shared.SignalR;

/// <summary>
/// Hub method names used by both the server and the kiosk client. A name that differs between
/// the two sides silently drops the call, so new names live here rather than as string literals
/// on each side. Never rename one: kiosks in the field still use the old value until they update.
/// </summary>
public static class ClientHubMethods
{
    /// <summary>Server to kiosk, client result: returns a <c>ClientLogTailDTO</c>.</summary>
    public const string RequestLogTail = "RequestLogTail";
}
