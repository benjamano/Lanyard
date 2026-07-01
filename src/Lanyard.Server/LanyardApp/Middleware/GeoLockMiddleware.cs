using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace Lanyard.App.Middleware;

public sealed class GeoLockMiddleware : IDisposable
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GeoLockMiddleware> _logger;
    private readonly IMemoryCache _cache;
    private readonly DatabaseReader? _reader;
    private readonly HashSet<string> _allowedCountries;
    private readonly HashSet<string> _allowedIPs;
    private readonly TimeSpan _cacheDuration;

    private static readonly string[] _bypassPrefixes = ["/blocked", "/_framework", "/_blazor", "/favicon"];

    public GeoLockMiddleware(
        RequestDelegate next,
        IConfiguration config,
        ILogger<GeoLockMiddleware> logger,
        IMemoryCache cache)
    {
        _next = next;
        _logger = logger;
        _cache = cache;

        _allowedCountries = config.GetSection("GeoLock:AllowedCountries").Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? ["GB"];

        _allowedIPs = config.GetSection("GeoLock:AllowedIPs").Get<string[]>()?.ToHashSet()
            ?? [];

        int cacheMins = config.GetValue("GeoLock:CacheDurationMinutes", 60);
        _cacheDuration = TimeSpan.FromMinutes(cacheMins);

        string dbPath = config.GetValue("GeoLock:DatabasePath", "GeoLite2-Country.mmdb")!;

        if (File.Exists(dbPath))
        {
            _reader = new DatabaseReader(dbPath);
            _logger.LogInformation("GeoLock: Loaded database from {Path}", dbPath);
        }
        else
        {
            _logger.LogWarning("GeoLock: Database file not found at '{Path}'. All requests will be allowed.", dbPath);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Always allow requests to the blocked page and Blazor framework assets
        string path = context.Request.Path.Value ?? "";
        if (_bypassPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (_reader == null || IsAllowed(context.Connection.RemoteIpAddress))
        {
            await _next(context);
            return;
        }

        _logger.LogInformation("GeoLock: Blocked request from {IP} to {Path}",
            context.Connection.RemoteIpAddress, path);

        context.Response.Redirect("/blocked");
    }

    private bool IsAllowed(IPAddress? ip)
    {
        if (ip == null) return true;

        // Normalise IPv4-mapped IPv6 (::ffff:x.x.x.x)
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // Always allow loopback and private ranges (RFC 1918 / link-local)
        if (IPAddress.IsLoopback(ip) || IsPrivateIPv4(ip))
            return true;

        string ipStr = ip.ToString();

        if (_allowedIPs.Contains(ipStr))
            return true;

        return _cache.GetOrCreate($"geolock:{ipStr}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheDuration;
            return LookupCountry(ip, ipStr);
        });
    }

    private bool LookupCountry(IPAddress ip, string ipStr)
    {
        try
        {
            var response = _reader!.Country(ip);
            string? countryCode = response.Country.IsoCode;
            bool allowed = _allowedCountries.Contains(countryCode ?? "");

            if (!allowed)
                _logger.LogInformation("GeoLock: IP {IP} resolved to country '{Country}' — blocked", ipStr, countryCode);

            return allowed;
        }
        catch (AddressNotFoundException)
        {
            _logger.LogWarning("GeoLock: IP {IP} not found in database — allowing", ipStr);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeoLock: Unexpected error looking up {IP} — allowing", ipStr);
            return true;
        }
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254); // link-local
    }

    public void Dispose() => _reader?.Dispose();
}
