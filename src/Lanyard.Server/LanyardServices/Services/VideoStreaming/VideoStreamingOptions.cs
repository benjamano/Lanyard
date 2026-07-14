using System.Text.Json;

namespace Lanyard.Application.Services.VideoStreaming;

/// <summary>
/// Bound from the "VideoStreaming" section of appsettings. Lets a deployment point the
/// cross-client WebRTC streams at a STUN and/or TURN server without a rebuild — the reliable
/// fix when direct peer-to-peer is blocked by a firewall, client isolation, or routing.
/// </summary>
public class VideoStreamingOptions
{
    public List<IceServerOption> IceServers { get; set; } = [];

    /// <summary>
    /// Serialises the configured servers into the exact JSON shape an
    /// <c>RTCPeerConnection</c> <c>iceServers</c> array expects (lowercase keys). Returns "[]"
    /// when nothing is configured, in which case the browser applies its STUN fallback.
    /// </summary>
    public string ToIceServersJson()
    {
        List<object> servers = [];

        foreach (IceServerOption server in IceServers)
        {
            if (server.Urls.Count == 0)
            {
                continue;
            }

            Dictionary<string, object> entry = new() { ["urls"] = server.Urls };

            if (!string.IsNullOrEmpty(server.Username))
            {
                entry["username"] = server.Username;
            }

            if (!string.IsNullOrEmpty(server.Credential))
            {
                entry["credential"] = server.Credential;
            }

            servers.Add(entry);
        }

        return JsonSerializer.Serialize(servers);
    }
}

public class IceServerOption
{
    /// <summary>One or more URLs for this server, e.g. "stun:host:3478" or "turn:host:3478".</summary>
    public List<string> Urls { get; set; } = [];

    public string? Username { get; set; }

    public string? Credential { get; set; }
}
