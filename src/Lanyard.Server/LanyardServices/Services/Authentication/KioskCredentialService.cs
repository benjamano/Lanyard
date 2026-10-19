using System.Security.Cryptography;
using System.Text;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

// Issues and checks per-kiosk credentials. A credential is "lk1.<credential id>.<secret>": the id
// finds the row in one lookup, and only a SHA-256 of the secret is stored. A plain hash is right
// here (as for clock-in terminals): the secret is 256 random bits, so there is nothing to
// brute-force, and a slow hash would cost every kiosk request.
//
// Singleton, and uses the system factory: it runs during authentication, before anyone knows which
// company the caller belongs to - finding that out is its job.
public class KioskCredentialService(
    ISystemDbContextFactory factory,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<KioskCredentialService> logger) : IKioskCredentialService
{
    // A revoke evicts the entry at once (see Evict); the expiry only bounds how stale a
    // company being switched off can be.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    // LastUsedUtc answers "is this kiosk still using its credential?", not "when exactly".
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    private const int SecretBytes = 32;

    // The credential couldn't be checked at all (the database didn't answer). Not the same as a
    // dead credential: KioskAuthenticationHandler fails the request without telling the kiosk its
    // credential is invalid, so a database blip never makes a kiosk throw its credential away.
    public const string CheckUnavailableError = "The kiosk credential couldn't be checked right now.";

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskCredentialService> _logger = logger;

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string FormatToken(Guid credentialId, string secret) =>
        $"{KioskAuthenticationDefaults.CredentialPrefix}.{credentialId:N}.{secret}";

    public static bool TryParseToken(string? rawToken, out Guid credentialId, out string secret)
    {
        credentialId = Guid.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        string[] parts = rawToken.Trim().Split('.');

        if (parts.Length != 3
            || parts[0] != KioskAuthenticationDefaults.CredentialPrefix
            || !Guid.TryParseExact(parts[1], "N", out credentialId)
            || string.IsNullOrEmpty(parts[2]))
        {
            credentialId = Guid.Empty;
            return false;
        }

        secret = parts[2];
        return true;
    }

    public static void Evict(IMemoryCache cache, Guid credentialId) => cache.Remove(CacheKey(credentialId));

    public async Task<Result<KioskIdentity>> ValidateAsync(string? rawToken, CancellationToken cancellationToken = default)
    {
        if (!TryParseToken(rawToken, out Guid credentialId, out string secret))
        {
            return Result<KioskIdentity>.Fail("The kiosk credential is malformed.");
        }

        Result<CachedCredential> loaded = await LoadAsync(credentialId, cancellationToken);

        if (!loaded.IsSuccess || loaded.Data is null)
        {
            return Result<KioskIdentity>.Fail(loaded.Error ?? "The kiosk credential is not valid.");
        }

        byte[] expected = Encoding.ASCII.GetBytes(loaded.Data.SecretHash);
        byte[] provided = Encoding.ASCII.GetBytes(HashSecret(secret));

        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            return Result<KioskIdentity>.Fail("The kiosk credential's secret does not match.");
        }

        await TouchAsync(loaded.Data, cancellationToken);

        return Result<KioskIdentity>.Ok(loaded.Data.Identity);
    }

    public async Task<Result<KioskIdentity>> ValidateByIdAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        Result<CachedCredential> loaded = await LoadAsync(credentialId, cancellationToken);

        return loaded.IsSuccess && loaded.Data is not null
            ? Result<KioskIdentity>.Ok(loaded.Data.Identity)
            : Result<KioskIdentity>.Fail(loaded.Error ?? "The kiosk credential is not valid.");
    }

    public async Task<Result<int>> GetClientCompanyIdAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            int? companyId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);

            return companyId is int id
                ? Result<int>.Ok(id)
                : Result<int>.Fail("No such kiosk.");
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to look up the kiosk: {ex.Message}");
        }
    }

    public async Task<Result<IssuedKioskCredential>> IssueAsync(Guid clientId, ClientCredentialSource source, string? createdByUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            bool clientExists = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == clientId, cancellationToken);

            if (!clientExists)
            {
                return Result<IssuedKioskCredential>.Fail("That kiosk doesn't exist.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            List<ClientCredential> existing = await ctx.ClientCredentials
                .TagWithCallSite()
                .Where(x => x.ClientId == clientId && x.IsActive)
                .ToListAsync(cancellationToken);

            foreach (ClientCredential old in existing)
            {
                old.IsActive = false;
                old.RevokedUtc = now;
                old.RevokedByUserId = createdByUserId;
                old.RevokeReason = "Replaced by a new credential";
            }

            string secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));

            ClientCredential credential = new()
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                SecretHash = HashSecret(secret),
                Source = source,
                CreatedUtc = now,
                CreatedByUserId = createdByUserId,
                ActivatedUtc = now,
                IsActive = true
            };

            ctx.ClientCredentials.Add(credential);

            await ctx.SaveChangesAsync(cancellationToken);

            foreach (ClientCredential old in existing)
            {
                Evict(_cache, old.Id);
            }

            _logger.LogInformation("Issued kiosk credential {CredentialId} for client {ClientId} ({Source}), replacing {ReplacedCount}", credential.Id, clientId, source, existing.Count);

            return Result<IssuedKioskCredential>.Ok(new IssuedKioskCredential(credential.Id, clientId, FormatToken(credential.Id, secret)));
        }
        catch (Exception ex)
        {
            return Result<IssuedKioskCredential>.Fail($"Failed to issue a kiosk credential: {ex.Message}");
        }
    }

    private async Task<Result<CachedCredential>> LoadAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(credentialId), out CachedCredential? cached) && cached is not null)
        {
            return Result<CachedCredential>.Ok(cached);
        }

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            var row = await ctx.ClientCredentials
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == credentialId)
                .Select(x => new
                {
                    x.ClientId,
                    x.SecretHash,
                    x.IsActive,
                    x.ActivatedUtc,
                    x.LastUsedUtc,
                    x.Client!.CompanyId,
                    CompanyActive = ctx.Companies.Any(c => c.Id == x.Client!.CompanyId && c.IsActive)
                })
                .FirstOrDefaultAsync(cancellationToken);

            // Not cached when invalid: a credential can't come back to life, but caching misses
            // would let a flood of made-up ids fill the cache.
            if (row is null)
            {
                return Result<CachedCredential>.Fail("No such kiosk credential.");
            }

            if (!row.IsActive)
            {
                return Result<CachedCredential>.Fail("The kiosk credential has been revoked.");
            }

            if (row.ActivatedUtc is null)
            {
                return Result<CachedCredential>.Fail("The kiosk credential is waiting for the kiosk to confirm it.");
            }

            if (!row.CompanyActive)
            {
                return Result<CachedCredential>.Fail("The kiosk's company is not active.");
            }

            CachedCredential credential = new(new KioskIdentity(row.ClientId, row.CompanyId, credentialId), row.SecretHash, row.LastUsedUtc);

            _cache.Set(CacheKey(credentialId), credential, CacheFor);

            return Result<CachedCredential>.Ok(credential);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check kiosk credential {CredentialId}", credentialId);
            return Result<CachedCredential>.Fail(CheckUnavailableError);
        }
    }

    private async Task TouchAsync(CachedCredential credential, CancellationToken cancellationToken)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (credential.LastUsedUtc is DateTime last && now - last < LastUsedThrottle)
        {
            return;
        }

        credential.LastUsedUtc = now;

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            await ctx.ClientCredentials
                .TagWithCallSite()
                .Where(x => x.Id == credential.Identity.CredentialId)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastUsedUtc, now), cancellationToken);
        }
        catch (Exception ex)
        {
            // Bookkeeping only - never a reason to turn a valid kiosk away.
            _logger.LogWarning("Failed to record use of kiosk credential {CredentialId}: {Error}", credential.Identity.CredentialId, ex.Message);
        }
    }

    private static string CacheKey(Guid credentialId) => $"kiosk-credential:{credentialId:N}";

    private sealed class CachedCredential(KioskIdentity identity, string secretHash, DateTime? lastUsedUtc)
    {
        public KioskIdentity Identity { get; } = identity;
        public string SecretHash { get; } = secretHash;
        public DateTime? LastUsedUtc { get; set; } = lastUsedUtc;
    }
}
