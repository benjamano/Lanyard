using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Lanyard.Application.Services.Demo;

// Keeps server-side fetches of visitor-supplied URLs (the demo's "get my branding from my website")
// on the public internet. Without it, anyone on the public demo could make the server request
// cloud metadata (169.254.169.254), Railway's private network or localhost services (SSRF).
//
// The check runs in the socket connect callback, against the address actually being connected to,
// for every connection including after redirects - so neither a redirect nor a DNS answer that
// changes between lookup and connect ("DNS rebinding") can reach a private address.
public static class PublicAddressGuard
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        // A proxy would make the connect callback see the proxy, not the real destination.
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 4,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        DnsEndPoint endPoint = context.DnsEndPoint;

        if (endPoint.Port is not (80 or 443))
        {
            throw new HttpRequestException("Only standard web ports (80 and 443) can be fetched.");
        }

        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct);

        // Every address the name resolves to must be public, not just the one we'd pick - a name
        // that points anywhere private isn't a normal public website.
        if (addresses.Length == 0 || !addresses.All(IsPublic))
        {
            throw new HttpRequestException("That address isn't a public website.");
        }

        // Each (already checked) address in turn, IPv4 first: plenty of hosts publish IPv6
        // addresses that aren't reachable from every network.
        SocketException? lastError = null;

        foreach (IPAddress address in addresses.OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Couldn't connect to that website.", lastError);
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();

            return !(b[0] == 0                                   // "this" network
                || b[0] == 10                                    // private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // carrier-grade NAT
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, incl. cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // documentation
                || (b[0] == 192 && b[1] == 168)                  // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // documentation
                || b[0] >= 224);                                 // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();

            return !(IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6None)            // ::
                || address.IsIPv6LinkLocal                       // fe80::/10
                || address.IsIPv6SiteLocal                       // fec0::/10
                || address.IsIPv6Multicast                       // ff00::/8
                || (b[0] & 0xFE) == 0xFC                         // fc00::/7 unique local (Railway's private network)
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // 2001:db8::/32 documentation
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) // 2001::/32 Teredo (tunnels to IPv4)
                || (b[0] == 0x20 && b[1] == 0x02)                // 2002::/16 6to4 (embeds an IPv4 address)
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)); // 64:ff9b::/96 NAT64
        }

        return false;
    }
}
