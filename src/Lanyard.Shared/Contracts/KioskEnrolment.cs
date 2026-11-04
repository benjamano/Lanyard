namespace Lanyard.Shared.Contracts;

// How a kiosk gets its own credential (see KioskEnrolmentController on the server):
//  - A new kiosk POSTs Requests, shows the pairing code, and polls Request(id) with the poll token
//    in EnrolmentTokenHeader until an Admin approves it and the poll returns its credential.
//  - A kiosk the server already knows, still holding the old shared secret, POSTs LegacyExchange
//    once and gets its credential straight away, while the server allows it.
public static class KioskEnrolmentRoutes
{
    public const string Requests = "api/kiosk-enrolment/requests";
    public const string RequestTemplate = "api/kiosk-enrolment/requests/{requestId:guid}";
    public const string LegacyExchange = "api/kiosk-enrolment/legacy-exchange";

    public static string Request(Guid requestId) => $"api/kiosk-enrolment/requests/{requestId}";

    public const string EnrolmentTokenHeader = "X-Lanyard-Enrolment-Token";
}

public sealed record KioskEnrolmentRequestDTO(Guid ClientId, string? MachineName, string? ClientVersion);

// PairingCode is shown to people as XXXX-XXXX. PollToken proves to the server that a poll comes
// from the kiosk that asked; keep it in memory only.
public sealed record KioskEnrolmentStartedDTO(Guid RequestId, string PairingCode, string PollToken, DateTime ExpiresUtc, int PollIntervalSeconds);

public enum KioskEnrolmentState
{
    // Still waiting for an Admin. Keep polling.
    Pending = 0,

    // Approved: Credential is set. It is only ever returned once.
    Approved = 1,

    // An Admin turned it down. Start again with a new request.
    Rejected = 2,

    // The code ran out (or was already collected). Start again with a new request.
    Expired = 3
}

public sealed record KioskEnrolmentStatusDTO(KioskEnrolmentState State, Guid? ClientId, string? Credential);

public sealed record KioskCredentialIssuedDTO(Guid ClientId, string Credential);
