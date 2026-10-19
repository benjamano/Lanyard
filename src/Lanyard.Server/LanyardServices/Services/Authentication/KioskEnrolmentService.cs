using System.Security.Claims;
using System.Security.Cryptography;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

// Pairing a kiosk. The kiosk asks (StartAsync) and shows a code; an Admin who can see the screen
// types the code in, which proves someone is physically at the kiosk; the kiosk then collects its
// own credential (PollAsync). Requests aren't company-owned - nobody knows the company until an
// Admin approves - so they go through the system factory, and the Admin's company comes from
// ITenantContext. Pending requests are never listed, so one company can't see another's kiosks.
public class KioskEnrolmentService(
    ISystemDbContextFactory factory,
    IKioskCredentialService credentialService,
    IClientService clientService,
    ITenantContext tenant,
    AuthenticationStateProvider authenticationStateProvider,
    IConfiguration configuration,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<KioskEnrolmentService> logger) : IKioskEnrolmentService
{
    // Crockford base32: no I, L, O or U, so nothing on the screen can be misread as something else.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 8;

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public const int PollIntervalSeconds = 5;

    // 8 characters is 40 bits, and a code lives 15 minutes, so guessing is hopeless anyway; this
    // stops a signed-in Admin's session being used to try codes in bulk.
    private const int MaxWrongCodes = 10;
    private static readonly TimeSpan WrongCodeWindow = TimeSpan.FromMinutes(15);

    private const int MaxNameLength = 100;

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IKioskCredentialService _credentialService = credentialService;
    private readonly IClientService _clientService = clientService;
    private readonly ITenantContext _tenant = tenant;
    private readonly AuthenticationStateProvider _authenticationStateProvider = authenticationStateProvider;
    private readonly IConfiguration _configuration = configuration;
    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskEnrolmentService> _logger = logger;

    // Upper case, with spaces and dashes removed, and the letters people confuse with digits mapped
    // the way Crockford base32 says to. Null when it can't be a code at all.
    public static string? NormaliseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string cleaned = new(code
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(c => char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                char other => other
            })
            .ToArray());

        return cleaned.Length == CodeLength && cleaned.All(CodeAlphabet.Contains) ? cleaned : null;
    }

    public static string FormatCode(string normalised) => $"{normalised[..4]}-{normalised[4..]}";

    public async Task<Result<KioskEnrolmentStartedDTO>> StartAsync(KioskEnrolmentRequestDTO request, string? requestIp, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.ClientId == Guid.Empty)
            {
                return Result<KioskEnrolmentStartedDTO>.Fail("A kiosk id is required.");
            }

            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            // A kiosk asking again (restarted, or its code ran out) replaces its old code.
            List<ClientEnrolmentRequest> earlier = await ctx.ClientEnrolmentRequests
                .TagWithCallSite()
                .Where(x => x.ClientId == request.ClientId && x.Status == ClientEnrolmentStatus.Pending)
                .ToListAsync(cancellationToken);

            foreach (ClientEnrolmentRequest old in earlier)
            {
                old.Status = ClientEnrolmentStatus.Expired;
            }

            string code = await NewUniqueCodeAsync(ctx, now, cancellationToken);
            string pollToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

            ClientEnrolmentRequest row = new()
            {
                Id = Guid.NewGuid(),
                ClientId = request.ClientId,
                PairingCodeHash = KioskCredentialService.HashSecret(code),
                PollTokenHash = KioskCredentialService.HashSecret(pollToken),
                MachineName = Truncate(request.MachineName, 100),
                ReportedVersion = ClientVersionFormat.TryNormalize(request.ClientVersion, out string version) ? version : null,
                RequestIp = Truncate(requestIp, 64),
                CreatedUtc = now,
                ExpiresUtc = now.Add(CodeLifetime),
                Status = ClientEnrolmentStatus.Pending
            };

            ctx.ClientEnrolmentRequests.Add(row);
            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Kiosk {ClientId} ({MachineName}, {IpAddress}) asked to be paired; request {RequestId}", request.ClientId, row.MachineName, row.RequestIp, row.Id);

            return Result<KioskEnrolmentStartedDTO>.Ok(new KioskEnrolmentStartedDTO(row.Id, FormatCode(code), pollToken, row.ExpiresUtc, PollIntervalSeconds));
        }
        catch (Exception ex)
        {
            return Result<KioskEnrolmentStartedDTO>.Fail($"Failed to start pairing: {ex.Message}");
        }
    }

    public async Task<Result<KioskEnrolmentStatusDTO>> PollAsync(Guid requestId, string? pollToken, CancellationToken cancellationToken = default)
    {
        const string notFound = "No such pairing request.";

        try
        {
            if (string.IsNullOrEmpty(pollToken))
            {
                return Result<KioskEnrolmentStatusDTO>.Fail(notFound);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            ClientEnrolmentRequest? row = await ctx.ClientEnrolmentRequests
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);

            if (row is null || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(row.PollTokenHash),
                    System.Text.Encoding.ASCII.GetBytes(KioskCredentialService.HashSecret(pollToken))))
            {
                return Result<KioskEnrolmentStatusDTO>.Fail(notFound);
            }

            switch (row.Status)
            {
                case ClientEnrolmentStatus.Pending when Now >= row.ExpiresUtc:
                    row.Status = ClientEnrolmentStatus.Expired;
                    await ctx.SaveChangesAsync(cancellationToken);
                    return Result<KioskEnrolmentStatusDTO>.Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Expired, null, null));

                case ClientEnrolmentStatus.Pending:
                    return Result<KioskEnrolmentStatusDTO>.Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Pending, null, null));

                case ClientEnrolmentStatus.Rejected:
                    return Result<KioskEnrolmentStatusDTO>.Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Rejected, null, null));

                case ClientEnrolmentStatus.Approved:
                    break;

                default:
                    // Collected or Expired: the credential has gone (or never will), so start again.
                    return Result<KioskEnrolmentStatusDTO>.Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Expired, null, null));
            }

            // Marked collected first, so later polls get nothing. A kiosk polls one request at a
            // time; if two polls ever did overlap, the second credential would revoke the first
            // (IssueAsync), so the worst case is a kiosk that has to pair again, never two live ones.
            row.Status = ClientEnrolmentStatus.Collected;
            await ctx.SaveChangesAsync(cancellationToken);

            Result<IssuedKioskCredential> issued = await _credentialService.IssueAsync(row.ClientId, ClientCredentialSource.Pairing, row.DecidedByUserId, cancellationToken);

            if (!issued.IsSuccess || issued.Data is null)
            {
                // Put it back so the kiosk's next poll tries again.
                row.Status = ClientEnrolmentStatus.Approved;
                await ctx.SaveChangesAsync(cancellationToken);

                return Result<KioskEnrolmentStatusDTO>.Fail(issued.Error ?? "Failed to issue the kiosk's credential.");
            }

            _logger.LogInformation("Kiosk {ClientId} collected its credential {CredentialId} (request {RequestId})", row.ClientId, issued.Data.CredentialId, row.Id);

            return Result<KioskEnrolmentStatusDTO>.Ok(new KioskEnrolmentStatusDTO(KioskEnrolmentState.Approved, row.ClientId, issued.Data.RawToken));
        }
        catch (Exception ex)
        {
            return Result<KioskEnrolmentStatusDTO>.Fail($"Failed to check the pairing request: {ex.Message}");
        }
    }

    public async Task<Result<KioskCredentialIssuedDTO>> ExchangeLegacySecretAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (KioskAuthenticationHandler.GetLegacySecretMode(_configuration) != KioskLegacySecretMode.AcceptAndExchange)
            {
                return Result<KioskCredentialIssuedDTO>.Fail("Exchanging the shared secret is switched off. Pair this kiosk instead.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            bool exists = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == clientId, cancellationToken);

            if (!exists)
            {
                return Result<KioskCredentialIssuedDTO>.Fail("This kiosk isn't known to the server. Pair it instead.");
            }

            // Once a kiosk has its own credential, the shared secret must never be able to get a
            // new one for it: anyone holding the secret and the kiosk's id could otherwise take
            // its identity and lock the real kiosk out. Revoked credentials count too - a revoked
            // kiosk has to be paired again by an Admin.
            bool hasHadCredential = await ctx.ClientCredentials
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.ClientId == clientId, cancellationToken);

            if (hasHadCredential)
            {
                _logger.LogWarning("Refused a shared-secret exchange for kiosk {ClientId}: it already has, or had, its own credential", clientId);

                return Result<KioskCredentialIssuedDTO>.Fail("This kiosk already has its own credential. Pair it again if it has lost it.");
            }

            Result<IssuedKioskCredential> issued = await _credentialService.IssueAsync(clientId, ClientCredentialSource.LegacyExchange, createdByUserId: null, cancellationToken);

            if (!issued.IsSuccess || issued.Data is null)
            {
                return Result<KioskCredentialIssuedDTO>.Fail(issued.Error ?? "Failed to issue the kiosk's credential.");
            }

            _logger.LogInformation("Kiosk {ClientId} exchanged the shared secret for its own credential {CredentialId}", clientId, issued.Data.CredentialId);

            return Result<KioskCredentialIssuedDTO>.Ok(new KioskCredentialIssuedDTO(clientId, issued.Data.RawToken));
        }
        catch (Exception ex)
        {
            return Result<KioskCredentialIssuedDTO>.Fail($"Failed to exchange the shared secret: {ex.Message}");
        }
    }

    public async Task<Result<PendingKioskEnrolment>> FindPendingAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<(string UserId, int CompanyId)> caller = await GetAdminAsync();

            if (!caller.IsSuccess)
            {
                return Result<PendingKioskEnrolment>.Fail(caller.Error!);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Result<ClientEnrolmentRequest> found = await FindByCodeAsync(ctx, pairingCode, caller.Data.UserId, cancellationToken);

            if (!found.IsSuccess || found.Data is null)
            {
                return Result<PendingKioskEnrolment>.Fail(found.Error!);
            }

            ClientEnrolmentRequest row = found.Data;

            Client? existing = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == row.ClientId, cancellationToken);

            if (existing is not null && existing.CompanyId != caller.Data.CompanyId)
            {
                return Result<PendingKioskEnrolment>.Fail(OtherCompanyError);
            }

            return Result<PendingKioskEnrolment>.Ok(new PendingKioskEnrolment(
                row.ClientId, row.MachineName, row.ReportedVersion, row.RequestIp, row.CreatedUtc, row.ExpiresUtc,
                existing?.Name, existing?.LocationId));
        }
        catch (Exception ex)
        {
            return Result<PendingKioskEnrolment>.Fail($"Failed to look up the pairing code: {ex.Message}");
        }
    }

    public async Task<Result<Guid>> ApproveAsync(string pairingCode, string name, int? locationId, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<(string UserId, int CompanyId)> caller = await GetAdminAsync();

            if (!caller.IsSuccess)
            {
                return Result<Guid>.Fail(caller.Error!);
            }

            (string userId, int companyId) = caller.Data;

            string trimmedName = name?.Trim() ?? string.Empty;

            if (trimmedName.Length == 0)
            {
                return Result<Guid>.Fail("Give the kiosk a name.");
            }

            if (trimmedName.Length > MaxNameLength)
            {
                return Result<Guid>.Fail($"Keep the name to {MaxNameLength} characters or fewer.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            if (locationId is int location
                && !await ctx.Locations.AsNoTracking().TagWithCallSite().AnyAsync(x => x.Id == location && x.CompanyId == companyId && x.IsActive, cancellationToken))
            {
                return Result<Guid>.Fail("That location isn't one of your company's active locations.");
            }

            Result<ClientEnrolmentRequest> found = await FindByCodeAsync(ctx, pairingCode, userId, cancellationToken);

            if (!found.IsSuccess || found.Data is null)
            {
                return Result<Guid>.Fail(found.Error!);
            }

            ClientEnrolmentRequest row = found.Data;

            Client? client = await ctx.Clients
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == row.ClientId, cancellationToken);

            if (client is not null && client.CompanyId != companyId)
            {
                return Result<Guid>.Fail(OtherCompanyError);
            }

            DateTime now = Now;

            if (client is null)
            {
                // CompanyId set here, never left for ApplicationDbContext to default: on the system
                // factory a zero would land the kiosk in the seed company.
                client = new Client
                {
                    Id = row.ClientId,
                    CompanyId = companyId,
                    Name = trimmedName,
                    CreateDate = now,
                    ReportedClientVersion = row.ReportedVersion,
                    ClientVersionReportedAt = row.ReportedVersion is null ? null : now
                };

                ctx.Clients.Add(client);
            }
            else
            {
                client.Name = trimmedName;
                client.LastUpdateDate = now;
            }

            row.Status = ClientEnrolmentStatus.Approved;
            row.DecidedByUserId = userId;
            row.DecidedUtc = now;
            row.ApprovedCompanyId = companyId;

            await ctx.SaveChangesAsync(cancellationToken);

            if (client.LocationId != locationId)
            {
                Result<bool> setLocation = await _clientService.SetClientLocationAsync(client.Id, locationId);

                if (!setLocation.IsSuccess)
                {
                    // The kiosk is paired either way; only the location didn't stick.
                    _logger.LogWarning("Paired kiosk {ClientId} but couldn't set its location {LocationId}: {Error}", client.Id, locationId, setLocation.Error);
                }
            }

            _logger.LogInformation("Kiosk {ClientId} ({Name}) approved by {UserId} for company {CompanyId}", client.Id, trimmedName, userId, companyId);

            return Result<Guid>.Ok(client.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid>.Fail($"Failed to pair the kiosk: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RejectAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        try
        {
            Result<(string UserId, int CompanyId)> caller = await GetAdminAsync();

            if (!caller.IsSuccess)
            {
                return Result<bool>.Fail(caller.Error!);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Result<ClientEnrolmentRequest> found = await FindByCodeAsync(ctx, pairingCode, caller.Data.UserId, cancellationToken);

            if (!found.IsSuccess || found.Data is null)
            {
                return Result<bool>.Fail(found.Error!);
            }

            found.Data.Status = ClientEnrolmentStatus.Rejected;
            found.Data.DecidedByUserId = caller.Data.UserId;
            found.Data.DecidedUtc = Now;

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Pairing request {RequestId} for kiosk {ClientId} rejected by {UserId}", found.Data.Id, found.Data.ClientId, caller.Data.UserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to reject the kiosk: {ex.Message}");
        }
    }

    private const string OtherCompanyError = "This kiosk belongs to another company, so it can't be paired here.";

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    private async Task<Result<(string UserId, int CompanyId)>> GetAdminAsync()
    {
        ClaimsPrincipal user = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;

        string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (user.Identity?.IsAuthenticated != true || userId is null || !user.IsInRole("Admin"))
        {
            return Result<(string, int)>.Fail("Only Admins can pair kiosks.");
        }

        if (_tenant.IsSystem || _tenant.CompanyId is not int companyId)
        {
            return Result<(string, int)>.Fail("Sign in to a company to pair kiosks for it.");
        }

        return Result<(string, int)>.Ok((userId, companyId));
    }

    private async Task<Result<ClientEnrolmentRequest>> FindByCodeAsync(ApplicationDbContext ctx, string pairingCode, string userId, CancellationToken cancellationToken)
    {
        const string notFound = "That code doesn't match a kiosk waiting to be paired. Check the code on the kiosk's screen; codes last 15 minutes.";

        string attemptsKey = $"kiosk-pairing-wrong-codes:{userId}";

        if (_cache.TryGetValue(attemptsKey, out int wrongSoFar) && wrongSoFar >= MaxWrongCodes)
        {
            return Result<ClientEnrolmentRequest>.Fail("Too many wrong codes. Wait a few minutes and try again.");
        }

        string? code = NormaliseCode(pairingCode);
        DateTime now = Now;

        ClientEnrolmentRequest? row = code is null
            ? null
            : await ctx.ClientEnrolmentRequests
                .TagWithCallSite()
                .Where(x => x.PairingCodeHash == KioskCredentialService.HashSecret(code) && x.Status == ClientEnrolmentStatus.Pending && x.ExpiresUtc > now)
                .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            _cache.Set(attemptsKey, wrongSoFar + 1, WrongCodeWindow);
            _logger.LogWarning("User {UserId} entered a pairing code that doesn't match a waiting kiosk", userId);

            return Result<ClientEnrolmentRequest>.Fail(notFound);
        }

        return Result<ClientEnrolmentRequest>.Ok(row);
    }

    private static async Task<string> NewUniqueCodeAsync(ApplicationDbContext ctx, DateTime now, CancellationToken cancellationToken)
    {
        // A clash needs two live codes out of 2^40; the loop is only there so it can never matter.
        while (true)
        {
            char[] chars = new char[CodeLength];

            for (int i = 0; i < CodeLength; i++)
            {
                chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }

            string code = new(chars);
            string hash = KioskCredentialService.HashSecret(code);

            bool inUse = await ctx.ClientEnrolmentRequests
                .AsNoTracking()
                .AnyAsync(x => x.PairingCodeHash == hash && x.Status == ClientEnrolmentStatus.Pending && x.ExpiresUtc > now, cancellationToken);

            if (!inUse)
            {
                return code;
            }
        }
    }

    private static string? Truncate(string? value, int max)
    {
        string? trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
