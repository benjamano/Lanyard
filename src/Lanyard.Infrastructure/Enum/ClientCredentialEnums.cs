namespace Lanyard.Infrastructure.Enum;

// How a kiosk credential came to exist. Shown on the kiosk's Security tab.
public enum ClientCredentialSource
{
    // An Admin typed the pairing code the kiosk showed.
    Pairing = 0,

    // A kiosk that already existed traded the old shared secret for its own credential.
    LegacyExchange = 1,

    // An Admin rotated the kiosk's previous credential.
    Rotation = 2
}

// How a kiosk proved who it was on its most recent hub connection, so an Admin can see which
// kiosks still rely on the shared secret before it is switched off.
public enum ClientAuthMethod
{
    LegacySecret = 0,
    Credential = 1
}

// Where a kiosk's request to be paired has got to. Only Pending can be approved or rejected.
public enum ClientEnrolmentStatus
{
    // Showing its pairing code, waiting for an Admin.
    Pending = 0,

    // An Admin approved it; the kiosk hasn't collected its credential yet.
    Approved = 1,

    // The kiosk collected its credential. Final.
    Collected = 2,

    Rejected = 3,

    // The code ran out before anyone approved it, or the kiosk asked for a new one.
    Expired = 4
}
