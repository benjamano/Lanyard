namespace Lanyard.Application.Services.Authentication;

public static class KioskAuthenticationDefaults
{
    public const string Scheme = "Kiosk";

    // The ClaimsIdentity.AuthenticationType of each way a kiosk can prove who it is.
    public const string CredentialAuthenticationType = "KioskCredential";
    public const string MediaTokenAuthenticationType = "KioskMediaToken";

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
