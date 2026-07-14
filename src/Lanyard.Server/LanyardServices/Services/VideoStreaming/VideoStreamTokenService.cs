using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Lanyard.Application.Services.VideoStreaming;

public class VideoStreamTokenService : IVideoStreamTokenService
{
    // Publisher tokens only need to survive one Edge cold-start; viewer tokens must live as
    // long as a kiosk runs, so their expiry slides forward on every successful validation.
    private static readonly TimeSpan PublisherTokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ViewerTokenLifetime = TimeSpan.FromHours(12);

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
    }

    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    public string IssuePublisherToken(Guid clientId)
    {
        return IssueToken(clientId, TokenPurpose.Publisher, PublisherTokenLifetime);
    }

    public bool ValidatePublisherToken(Guid clientId, string? token)
    {
        return ValidateToken(clientId, TokenPurpose.Publisher, token, slideExpiry: false);
    }

    public string IssueViewerToken(Guid clientId)
    {
        return IssueToken(clientId, TokenPurpose.Viewer, ViewerTokenLifetime);
    }

    public bool ValidateViewerToken(Guid clientId, string? token)
    {
        return ValidateToken(clientId, TokenPurpose.Viewer, token, slideExpiry: true);
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

    private bool ValidateToken(Guid clientId, TokenPurpose purpose, string? token, bool slideExpiry)
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

        if (slideExpiry)
        {
            entry.ExpiresUtc = DateTime.UtcNow.Add(ViewerTokenLifetime);
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
