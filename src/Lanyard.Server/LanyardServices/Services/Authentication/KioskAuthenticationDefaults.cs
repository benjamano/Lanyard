namespace Lanyard.Application.Services.Authentication;

public static class KioskAuthenticationDefaults
{
    public const string Scheme = "Kiosk";

    // The ClaimsIdentity.AuthenticationType of each way a kiosk can prove who it is. TenantContext
    // reads LegacySecret to keep an unidentified shared-secret caller unfiltered, as it was before.
    public const string CredentialAuthenticationType = "KioskCredential";
    public const string MediaTokenAuthenticationType = "KioskMediaToken";
    public const string LegacySecretAuthenticationType = "KioskLegacySecret";

    // The hub's endpoint policy: an authenticated kiosk that the server could tie to a Client row.
    public const string HubPolicy = "KioskHub";

    // Every kiosk credential starts with this, so a token from some future scheme is easy to tell apart.
    public const string CredentialPrefix = "lk1";

    // The query key for a media token, the one kiosk credential allowed in a URL (see KioskMediaTokenService).
    public const string MediaTokenQuery = "mt";
}

public static class KioskClaimTypes
{
    public const string ClientId = "lanyard:kiosk_client_id";
    public const string CredentialId = "lanyard:kiosk_credential_id";
}

// What the server does with the old shared kiosk secret while the fleet moves to per-kiosk
// credentials. Set with Clients:LegacySecretMode (env Clients__LegacySecretMode).
public enum KioskLegacySecretMode
{
    // Refused everywhere. The end state, once every kiosk has its own credential.
    Reject = 0,

    // Accepted (and logged), but a kiosk can't trade it for its own credential.
    Accept = 1,

    // Accepted, and a kiosk the server already knows can trade it for its own credential.
    AcceptAndExchange = 2
}
