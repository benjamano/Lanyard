using System.Net;

namespace Lanyard.Shared.LaserGame;

/// <summary>
/// Builds the BPF capture filter that lets Npcap drop everything except the laser game's UDP
/// traffic before it reaches the kiosk, e.g.
/// <c>(udp and (src host 192.168.0.10) and dst host 192.168.0.255) or (vlan and udp and ...)</c>.
/// The <c>vlan</c> half keeps 802.1Q-tagged venue networks working. There is no port clause: the
/// control box's port isn't configured anywhere, and the address pair already picks it out.
/// </summary>
public static class CaptureFilterBuilder
{
    /// <returns>
    /// The filter, or null when there is nothing safe to build: no source, or an address that
    /// doesn't parse. The kiosk then captures unfiltered, as it always did.
    /// </returns>
    public static string? Build(IEnumerable<string?> sourceIps, string? destinationIp)
    {
        List<IPAddress> sources = [];

        foreach (string? sourceIp in sourceIps)
        {
            if (string.IsNullOrWhiteSpace(sourceIp))
            {
                continue;
            }

            if (!IPAddress.TryParse(sourceIp.Trim(), out IPAddress? source))
            {
                return null;
            }

            sources.Add(source);
        }

        if (sources.Count == 0)
        {
            return null;
        }

        IPAddress? destination = null;

        if (!string.IsNullOrWhiteSpace(destinationIp) && !IPAddress.TryParse(destinationIp.Trim(), out destination))
        {
            return null;
        }

        // Only parsed addresses go into the expression, never the text an Admin typed.
        string match = $"udp and ({string.Join(" or ", sources.Select(s => $"src host {s}"))})";

        if (destination != null)
        {
            match += $" and dst host {destination}";
        }

        return $"({match}) or (vlan and {match})";
    }
}
