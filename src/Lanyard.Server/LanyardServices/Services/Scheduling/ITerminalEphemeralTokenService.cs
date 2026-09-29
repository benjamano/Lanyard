using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Short-lived, in-memory secrets for the clock-in terminal. In memory (not the database) because
// every one of them is worthless within minutes and the app runs as a single instance - the same
// trade-off VideoStreamTokenService makes. A restart simply makes a tablet show a fresh QR code and
// resets any PIN lockout; restarts are rare enough that this doesn't meaningfully help a guesser.
public interface ITerminalEphemeralTokenService
{
    // A one-use code that carries a manager's pairing request from the Blazor page to the
    // controller that sets the tablet's cookie. Valid for 5 minutes.
    string IssuePairingCode(int locationId, string terminalName, string issuedByUserId, bool signOutAfterPairing);
    PendingPairing? PeekPairingCode(string code);
    PendingPairing? ConsumePairingCode(string code);

    // The terminal shows a new code every QrRotationInterval; each is single-use and accepted a
    // little longer than it's shown, so a scan just as it changes still works.
    // holdKey is only needed for a code that has been held for sign-in (see below).
    string IssueQrNonce(Guid terminalId);
    Guid? PeekQrNonce(string nonce, string? holdKey = null);
    Guid? ConsumeQrNonce(string nonce, string? holdKey = null);

    // A phone that scans while signed out gets this one code held for QrSignInHold, once, so it
    // is still valid after signing in (and maybe 2FA). The scan itself still had to happen
    // within the code's minute, and the code is still single-use. Returns a hold key (null if
    // the code can't be held) that the scanning browser keeps in a cookie: once held, the code
    // only works alongside that key, so forwarding the link to someone who isn't at the
    // terminal doesn't give them a code that lasts ten minutes.
    string? HoldQrNonceForSignIn(string nonce);

    // Wrong-PIN throttling per person, across every terminal: each 5 wrong PINs locks them out,
    // for 30 seconds the first time and twice as long each time after, up to an hour. A right PIN
    // clears it; a day without a wrong one forgets it. Both return when the lock ends, or null.
    DateTime? PinLockedUntil(string userId);
    DateTime? RegisterPinFailure(string userId);
    void ClearPinFailures(string userId);
}
