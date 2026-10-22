using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Lanyard.Application.Services.VideoStreaming;

public class VideoStreamTokenService : IVideoStreamTokenService
{
    // Publisher tokens only need to survive one Edge cold-start, so they live in memory.
    private static readonly TimeSpan PublisherTokenLifetime = TimeSpan.FromMinutes(5);

    // Viewer tokens are in the kiosk page's URL, and the page now refuses to start a program
    // without one (Security Issue 9.3). The kiosk window reloads that same URL after a lost
    // connection, so the token has to outlive a server restart or deploy: it's signed with the
    // Data Protection key ring (kept in the database) instead of held in memory, and lasts long
    // enough for a kiosk that runs for weeks. The kiosk gets a new one each time it opens a window.
    private static readonly TimeSpan ViewerTokenLifetime = TimeSpan.FromDays(30);
    private const string ViewerPurpose = "Lanyard.KioskViewerToken.v1";

    private readonly ITimeLimitedDataProtector _viewerProtector;

    public VideoStreamTokenService(IDataProtectionProvider? dataProtectionProvider = null)
    {
        _viewerProtector = (dataProtectionProvider ?? new EphemeralDataProtectionProvider())
            .CreateProtector(ViewerPurpose)
            .ToTimeLimitedDataProtector();
    }

    private enum TokenPurpose
    {
        Publisher
    }

    private sealed class TokenEntry
    {
        public required Guid ClientId { get; init; }
        public required TokenPurpose Purpose { get; init; }
        public required DateTime ExpiresUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    public string IssuePublisherToken(Guid clientId)
    {
        return IssueToken(clientId, TokenPurpose.Publisher, PublisherTokenLifetime);
    }

    public bool ValidatePublisherToken(Guid clientId, string? token)
    {
        return ValidateToken(clientId, TokenPurpose.Publisher, token);
    }

    public string IssueViewerToken(Guid clientId)
    {
        return _viewerProtector.Protect(clientId.ToString("N"), DateTimeOffset.UtcNow.Add(ViewerTokenLifetime));
    }

    public bool ValidateViewerToken(Guid clientId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            // Unprotect throws for a tampered, expired or foreign-key token.
            return _viewerProtector.Unprotect(token) == clientId.ToString("N");
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private string IssueToken(Guid clientId, TokenPurpose purpose, TimeSpan lifetime)
    {
        PruneExpired();

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        _tokens[token] = new TokenEntry
        {
            ClientId = clientId,
            Purpose = purpose,
            ExpiresUtc = DateTime.UtcNow.Add(lifetime)
        };

        return token;
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

        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        return true;
    }

    private void PruneExpired()
    {
        DateTime nowUtc = DateTime.UtcNow;

        foreach (KeyValuePair<string, TokenEntry> pair in _tokens)
        {
            if (pair.Value.ExpiresUtc <= nowUtc)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }
}
