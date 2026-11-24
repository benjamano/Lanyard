using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Lanyard.Application.Services.VideoStreaming;

public class VideoStreamTokenService : IVideoStreamTokenService
{
    // Publisher tokens only need to survive one Edge cold-start; viewer tokens must live as
    // long as a kiosk runs, so their expiry slides forward on every successful validation.
    private static readonly TimeSpan PublisherTokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ViewerTokenLifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// The longest a viewer token works after a server restart. Tokens are held in memory, but a
    /// kiosk window keeps its token in its URL for as long as it stays open (the client leaves a
    /// running window alone when it reconnects), so viewer tokens are also signed with the app's
    /// persisted Data Protection keys. After a restart a signed token up to this old is taken back
    /// and slides as before; an older one needs the kiosk window reopening.
    /// </summary>
    public static readonly TimeSpan MaxViewerTokenAge = TimeSpan.FromDays(30);

    private const string ViewerPurpose = "Lanyard.KioskViewerToken.v1";

    private enum TokenPurpose
    {
        Publisher,
        Viewer
    }

    private sealed class TokenEntry
    {
        public required Guid ClientId { get; init; }
        public required TokenPurpose Purpose { get; init; }
        public required DateTime ExpiresUtc { get; set; }

        // A signed viewer token: kept (lapsed) until this, so one left idle past its 12 hours
        // can't come back through the restart path.
        public DateTime? SignedUntilUtc { get; init; }
    }

    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();
    private readonly ITimeLimitedDataProtector _viewerProtector;
    private readonly TimeProvider _timeProvider;

    // For tests and callers with no Data Protection: viewer tokens then don't outlive the instance.
    public VideoStreamTokenService() : this(new EphemeralDataProtectionProvider(), TimeProvider.System)
    {
    }

    public VideoStreamTokenService(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider)
    {
        _viewerProtector = dataProtectionProvider.CreateProtector(ViewerPurpose).ToTimeLimitedDataProtector();
        _timeProvider = timeProvider;
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public string IssuePublisherToken(Guid clientId)
    {
        PruneExpired();

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        _tokens[token] = new TokenEntry
        {
            ClientId = clientId,
            Purpose = TokenPurpose.Publisher,
            ExpiresUtc = Now.Add(PublisherTokenLifetime)
        };

        return token;
    }

    public bool ValidatePublisherToken(Guid clientId, string? token)
    {
        return ValidateToken(clientId, TokenPurpose.Publisher, token);
    }

    public string IssueViewerToken(Guid clientId)
    {
        PruneExpired();

        DateTime signedUntil = Now.Add(MaxViewerTokenAge);

        // The client id plus random bytes, so two tokens for one kiosk always differ.
        string token = _viewerProtector.Protect($"{clientId:N}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}",
            new DateTimeOffset(signedUntil, TimeSpan.Zero));

        _tokens[token] = new TokenEntry
        {
            ClientId = clientId,
            Purpose = TokenPurpose.Viewer,
            ExpiresUtc = Now.Add(ViewerTokenLifetime),
            SignedUntilUtc = signedUntil
        };

        return token;
    }

    public bool ValidateViewerToken(Guid clientId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // Not known here: a token this server issued before it restarted.
        if (!_tokens.ContainsKey(token) && SignedViewerUntil(clientId, token) is DateTime signedUntil)
        {
            _tokens.TryAdd(token, new TokenEntry
            {
                ClientId = clientId,
                Purpose = TokenPurpose.Viewer,
                ExpiresUtc = Now.Add(ViewerTokenLifetime),
                SignedUntilUtc = signedUntil
            });
        }

        return ValidateToken(clientId, TokenPurpose.Viewer, token);
    }

    // When a signed viewer token for this kiosk stops working, or null if it isn't one.
    private DateTime? SignedViewerUntil(Guid clientId, string token)
    {
        try
        {
            string payload = _viewerProtector.Unprotect(token, out DateTimeOffset expiration);

            return expiration.UtcDateTime > Now && payload.StartsWith($"{clientId:N}.", StringComparison.Ordinal)
                ? expiration.UtcDateTime
                : null;
        }
        catch (CryptographicException)
        {
            // Not a viewer token, tampered with, expired, or signed with a key this server doesn't have.
            return null;
        }
    }

    private bool ValidateToken(Guid clientId, TokenPurpose purpose, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (!_tokens.TryGetValue(token, out TokenEntry? entry))
        {
            return false;
        }

        if (entry.ClientId != clientId || entry.Purpose != purpose)
        {
            return false;
        }

        DateTime now = Now;

        if (entry.ExpiresUtc <= now)
        {
            if (entry.SignedUntilUtc is null)
            {
                _tokens.TryRemove(token, out _);
            }

            return false;
        }

        if (purpose == TokenPurpose.Viewer)
        {
            entry.ExpiresUtc = now.Add(ViewerTokenLifetime);
        }

        return true;
    }

    private void PruneExpired()
    {
        DateTime nowUtc = Now;

        foreach (KeyValuePair<string, TokenEntry> pair in _tokens)
        {
            if (pair.Value.ExpiresUtc <= nowUtc && (pair.Value.SignedUntilUtc is null || pair.Value.SignedUntilUtc <= nowUtc))
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }
}
