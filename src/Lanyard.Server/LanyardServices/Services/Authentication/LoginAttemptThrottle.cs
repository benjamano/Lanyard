using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

public interface ILoginAttemptThrottle
{
    // True when this IP has failed too often, for this username or overall, to try again yet.
    // Checked before the password: a refused attempt never reaches Identity, so it doesn't count
    // towards the account's own lockout.
    bool IsThrottled(IPAddress? ip, string username);

    void RecordFailure(IPAddress? ip, string username);

    void RecordSuccess(IPAddress? ip, string username);
}

// Stops one machine locking someone else out of their account. Identity locks an account after
// MaxFailedAccessAttempts (5) wrong passwords from anywhere, which protects against guessing but
// let anyone who knew a username (admin, say) lock it for 15 minutes with five requests. Here one
// IP gets PerUsernameLimit (4) failures for a username before its attempts for that username are
// refused without checking the password, so on its own it can never reach the account's limit;
// the person whose account it is, signing in from anywhere else, isn't affected. PerIpLimit caps
// one IP's failures across every username, which slows trying one password against many names.
//
// In memory, so per server instance and reset by a restart - fine for one Railway instance, and
// failing open on a restart only gives an attacker back what Identity's lockout already limits.
// A venue's staff share one public IP, so PerIpLimit is set well above a busy morning's typos.
public sealed class LoginAttemptThrottle(IMemoryCache cache, ILogger<LoginAttemptThrottle> logger) : ILoginAttemptThrottle
{
    public const int PerUsernameLimit = 4;
    public const int PerIpLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();

    public bool IsThrottled(IPAddress? ip, string username)
    {
        bool throttled = Count(PairKey(ip, username)) >= PerUsernameLimit || Count(IpKey(ip)) >= PerIpLimit;

        if (throttled)
        {
            logger.LogWarning("Refused a sign-in for {Username} from {RemoteIp}: too many failed attempts from that address", Normalise(username), ip?.ToString() ?? "unknown");
        }

        return throttled;
    }

    public void RecordFailure(IPAddress? ip, string username)
    {
        lock (_lock)
        {
            Increment(PairKey(ip, username));
            Increment(IpKey(ip));
        }
    }

    public void RecordSuccess(IPAddress? ip, string username) => cache.Remove(PairKey(ip, username));

    private int Count(string key) => cache.TryGetValue(key, out Counter? counter) && counter is not null ? counter.Value : 0;

    // A fixed window from the first failure, not sliding: retrying while throttled doesn't extend it.
    private void Increment(string key)
    {
        if (cache.TryGetValue(key, out Counter? counter) && counter is not null)
        {
            counter.Value++;
            return;
        }

        cache.Set(key, new Counter { Value = 1 }, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window, Size = 1 });
    }

    // Keyed on what was typed, whether or not the account exists, so a refusal says nothing about
    // which usernames are real.
    private static string PairKey(IPAddress? ip, string username) => $"login-fail:{ip?.ToString() ?? "unknown"}:{Normalise(username)}";

    private static string IpKey(IPAddress? ip) => $"login-fail-ip:{ip?.ToString() ?? "unknown"}";

    private static string Normalise(string username) => (username ?? string.Empty).Trim().ToUpperInvariant();

    private sealed class Counter
    {
        public int Value;
    }
}
