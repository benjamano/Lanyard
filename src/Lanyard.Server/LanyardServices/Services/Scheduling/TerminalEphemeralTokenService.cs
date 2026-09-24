using System.Collections.Concurrent;
using System.Security.Cryptography;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

public class TerminalEphemeralTokenService(TimeProvider timeProvider) : ITerminalEphemeralTokenService
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan QrRotationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QrNonceLifetime = TimeSpan.FromSeconds(60);
    public const int MaxPinFailures = 5;
    public static readonly TimeSpan PinFailureWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PinLockout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<string, PendingPairing> _pairingCodes = new();
    private readonly ConcurrentDictionary<string, (Guid TerminalId, DateTime ExpiresUtc)> _qrNonces = new();
    private readonly ConcurrentDictionary<(Guid TerminalId, string UserId), PinFailureState> _pinFailures = new();

    private sealed class PinFailureState
    {
        public List<DateTime> Failures { get; } = [];
        public DateTime? LockedUntilUtc { get; set; }
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public string IssuePairingCode(int locationId, string terminalName, string issuedByUserId, bool signOutAfterPairing)
    {
        Prune();

        string code = NewToken();
        _pairingCodes[code] = new PendingPairing(locationId, terminalName, issuedByUserId, signOutAfterPairing, Now.Add(PairingCodeLifetime));

        return code;
    }

    public PendingPairing? PeekPairingCode(string code) =>
        !string.IsNullOrEmpty(code) && _pairingCodes.TryGetValue(code, out PendingPairing? pairing) && pairing.ExpiresUtc > Now
            ? pairing
            : null;

    public PendingPairing? ConsumePairingCode(string code) =>
        !string.IsNullOrEmpty(code) && _pairingCodes.TryRemove(code, out PendingPairing? pairing) && pairing.ExpiresUtc > Now
            ? pairing
            : null;

    public string IssueQrNonce(Guid terminalId)
    {
        Prune();

        string nonce = NewToken();
        _qrNonces[nonce] = (terminalId, Now.Add(QrNonceLifetime));

        return nonce;
    }

    public Guid? PeekQrNonce(string nonce) =>
        !string.IsNullOrEmpty(nonce) && _qrNonces.TryGetValue(nonce, out (Guid TerminalId, DateTime ExpiresUtc) entry) && entry.ExpiresUtc > Now
            ? entry.TerminalId
            : null;

    public Guid? ConsumeQrNonce(string nonce) =>
        !string.IsNullOrEmpty(nonce) && _qrNonces.TryRemove(nonce, out (Guid TerminalId, DateTime ExpiresUtc) entry) && entry.ExpiresUtc > Now
            ? entry.TerminalId
            : null;

    public bool IsPinLocked(Guid terminalId, string userId)
    {
        if (!_pinFailures.TryGetValue((terminalId, userId), out PinFailureState? state))
        {
            return false;
        }

        lock (state)
        {
            return state.LockedUntilUtc is DateTime until && until > Now;
        }
    }

    public bool RegisterPinFailure(Guid terminalId, string userId)
    {
        PinFailureState state = _pinFailures.GetOrAdd((terminalId, userId), _ => new PinFailureState());
        DateTime now = Now;

        lock (state)
        {
            state.Failures.RemoveAll(x => now - x > PinFailureWindow);
            state.Failures.Add(now);

            if (state.Failures.Count >= MaxPinFailures)
            {
                state.LockedUntilUtc = now.Add(PinLockout);
                state.Failures.Clear();
            }

            return state.LockedUntilUtc is DateTime until && until > now;
        }
    }

    public void ClearPinFailures(Guid terminalId, string userId) =>
        _pinFailures.TryRemove((terminalId, userId), out _);

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private void Prune()
    {
        DateTime now = Now;

        foreach (KeyValuePair<string, PendingPairing> entry in _pairingCodes.Where(x => x.Value.ExpiresUtc <= now))
        {
            _pairingCodes.TryRemove(entry.Key, out _);
        }

        foreach (KeyValuePair<string, (Guid TerminalId, DateTime ExpiresUtc)> entry in _qrNonces.Where(x => x.Value.ExpiresUtc <= now))
        {
            _qrNonces.TryRemove(entry.Key, out _);
        }
    }
}
