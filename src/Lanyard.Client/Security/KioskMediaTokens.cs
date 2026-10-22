using Lanyard.Shared.Contracts;

namespace Lanyard.Client.Security;

// Song and audio-clip URLs are handed straight to Media Foundation to stream, and it can't send
// headers, so they carry a short-lived media token (?mt=) the server issues over the hub instead
// of the kiosk's credential. Cached and renewed well before it runs out.
public static class KioskMediaTokens
{
    private static readonly TimeSpan RenewWhenLessThan = TimeSpan.FromHours(2);
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static KioskMediaTokenDTO? _cached;

    // Set by SignalRClient while connected: asks the server for a token.
    public static Func<Task<KioskMediaTokenDTO?>>? Issuer { get; set; }

    public static async Task<string> AppendToAsync(string url)
    {
        string separator = url.Contains('?') ? "&" : "?";

        // A kiosk with no credential of its own has nothing the server will accept, and the old
        // shared secret no longer goes into URLs.
        if (KioskCredentials.Current is null)
        {
            return url;
        }

        string? token = await GetAsync();

        return token is null ? url : $"{url}{separator}{KioskCredentialQuery.MediaToken}={Uri.EscapeDataString(token)}";
    }

    // After a rotation the old token dies with the old credential.
    public static void Clear() => _cached = null;

    private static async Task<string?> GetAsync()
    {
        await Lock.WaitAsync();

        try
        {
            if (_cached is not null && _cached.ExpiresUtc - DateTime.UtcNow > RenewWhenLessThan)
            {
                return _cached.Token;
            }

            if (Issuer is Func<Task<KioskMediaTokenDTO?>> issuer)
            {
                try
                {
                    KioskMediaTokenDTO? issued = await issuer();

                    if (issued is not null && !string.IsNullOrEmpty(issued.Token))
                    {
                        _cached = issued;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Couldn't get a media token from the server: {ex.Message}");
                }
            }

            // An older token that hasn't run out yet still works.
            return _cached is not null && _cached.ExpiresUtc > DateTime.UtcNow ? _cached.Token : null;
        }
        finally
        {
            Lock.Release();
        }
    }
}
