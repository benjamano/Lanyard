namespace Lanyard.Shared.Contracts;

// A media token (see ClientHubMethods.IssueMediaToken). The kiosk appends it to a streaming URL as
// ?mt=, and asks for a new one well before ExpiresUtc.
public sealed record KioskMediaTokenDTO(string Token, DateTime ExpiresUtc);

public static class KioskCredentialQuery
{
    // Must match KioskAuthenticationDefaults.MediaTokenQuery on the server.
    public const string MediaToken = "mt";
}
