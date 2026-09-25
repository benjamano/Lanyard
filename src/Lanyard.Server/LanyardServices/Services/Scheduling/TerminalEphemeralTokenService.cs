using System.Collections.Concurrent;
using System.Security.Cryptography;
using Lanyard.Infrastructure.DTO.Scheduling;

namespace Lanyard.Application.Services.Scheduling;

public class TerminalEphemeralTokenService(TimeProvider timeProvider) : ITerminalEphemeralTokenService
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan QrRotationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QrNonceLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan QrSignInHold = TimeSpan.FromMinutes(10);
    public const int MaxPinFailures = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxPinLockout = TimeSpan.FromHours(1);
    public static readonly TimeSpan PinFailureMemory = TimeSpan.FromHours(24);

    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<string, PendingPairing> _pairingCodes = new();
    private readonly ConcurrentDictionary<string, QrNonce> _qrNonces = new();

    private sealed record QrNonce(Guid TerminalId, DateTime ExpiresUtc, bool HeldForSignIn);
    private readonly ConcurrentDictionary<string, PinFailureState> _pinFailures = new();

    private sealed class PinFailureState
    {
        public int Failures { get; set; }
        public int Lockouts { get; set; }
        public DateTime LastFailureUtc { get; set; }
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
        _qrNonces[nonce] = new QrNonce(terminalId, Now.Add(QrNonceLifetime), false);

        return nonce;
    }

    public Guid? PeekQrNonce(string nonce) =>
        !string.IsNullOrEmpty(nonce) && _qrNonces.TryGetValue(nonce, out QrNonce? entry) && entry.ExpiresUtc > Now
            ? entry.TerminalId
            : null;

    public Guid? ConsumeQrNonce(string nonce) =>
        !string.IsNullOrEmpty(nonce) && _qrNonces.TryRemove(nonce, out QrNonce? entry) && entry.ExpiresUtc > Now
            ? entry.TerminalId
            : null;

    public bool HoldQrNonceForSignIn(string nonce)
    {
        if (string.IsNullOrEmpty(nonce) || !_qrNonces.TryGetValue(nonce, out QrNonce? entry) || entry.ExpiresUtc <= Now || entry.HeldForSignIn)
        {
            return false;
        }

        return _qrNonces.TryUpdate(nonce, entry with { ExpiresUtc = Now.Add(QrSignInHold), HeldForSignIn = true }, entry);
    }

    public DateTime? PinLockedUntil(string userId)
    {
        if (!_pinFailures.TryGetValue(userId, out PinFailureState? state))
        {
            return null;
        }

        lock (state)
        {
            return state.LockedUntilUtc is DateTime until && until > Now ? until : null;
        }
    }

    public DateTime? RegisterPinFailure(string userId)
    {
        PrunePinFailures();

        PinFailureState state = _pinFailures.GetOrAdd(userId, _ => new PinFailureState());
        DateTime now = Now;

        lock (state)
        {
            // A day without a wrong PIN forgets the history; until then each lockout is twice the
            // last, so guessing someone's PIN slows to a handful of tries an hour.
            if (now - state.LastFailureUtc > PinFailureMemory)
            {
                state.Failures = 0;
                state.Lockouts = 0;
            }

            state.Failures++;
            state.LastFailureUtc = now;

            if (state.Failures >= MaxPinFailures)
            {
                state.Failures = 0;
                state.Lockouts++;

                TimeSpan lockout = PinLockout * Math.Pow(2, Math.Min(state.Lockouts - 1, 16));
                state.LockedUntilUtc = now.Add(lockout < MaxPinLockout ? lockout : MaxPinLockout);
            }

            return state.LockedUntilUtc is DateTime until && until > now ? until : null;
        }
    }

    public void ClearPinFailures(string userId) =>
        _pinFailures.TryRemove(userId, out _);

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private void PrunePinFailures()
    {
        DateTime now = Now;

        foreach (KeyValuePair<string, PinFailureState> entry in _pinFailures.Where(x => now - x.Value.LastFailureUtc > PinFailureMemory))
        {
            _pinFailures.TryRemove(entry.Key, out _);
        }
    }

    private void Prune()
    {
        DateTime now = Now;

        foreach (KeyValuePair<string, PendingPairing> entry in _pairingCodes.Where(x => x.Value.ExpiresUtc <= now))
        {
            _pairingCodes.TryRemove(entry.Key, out _);
        }

        foreach (KeyValuePair<string, QrNonce> entry in _qrNonces.Where(x => x.Value.ExpiresUtc <= now))
        {
            _qrNonces.TryRemove(entry.Key, out _);
        }
    }
}
