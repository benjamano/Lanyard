using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

// Short-lived, in-memory secrets for the clock-in terminal. In memory (not the database) because
// every one of them is worthless within minutes and the app runs as a single instance - the same
// trade-off VideoStreamTokenService makes. A restart simply makes a tablet show a fresh QR code and
// resets any PIN lockout, both harmless.
public interface ITerminalEphemeralTokenService
{
    // A one-use code that carries a manager's pairing request from the Blazor page to the
    // controller that sets the tablet's cookie. Valid for 5 minutes.
    string IssuePairingCode(int locationId, string terminalName, string issuedByUserId, bool signOutAfterPairing);
    PendingPairing? PeekPairingCode(string code);
    PendingPairing? ConsumePairingCode(string code);

    // The terminal shows a new code every QrRotationInterval; each is single-use and accepted a
    // little longer than it's shown, so a scan just as it changes still works.
    string IssueQrNonce(Guid terminalId);
    Guid? PeekQrNonce(string nonce);
    Guid? ConsumeQrNonce(string nonce);

    // Wrong-PIN throttling per person per terminal: 5 wrong attempts within a minute locks that
    // person out on that terminal for 30 seconds.
    bool IsPinLocked(Guid terminalId, string userId);
    bool RegisterPinFailure(Guid terminalId, string userId);
    void ClearPinFailures(Guid terminalId, string userId);
}
