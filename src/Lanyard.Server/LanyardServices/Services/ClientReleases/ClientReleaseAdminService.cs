using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.ClientReleases;

public sealed class ClientReleaseAdminService(
    IDbContextFactory<ApplicationDbContext> factory,
    ISystemDbContextFactory systemFactory,
    IClientReleaseCatalogService catalog,
    ITenantContext tenantContext,
    ISecurityService securityService,
    ILogger<ClientReleaseAdminService> logger) : IClientReleaseAdminService
{
    public const int MaxWithdrawReasonLength = 500;

    private const string NotAllowed = "Only a platform admin can manage kiosk releases.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ISystemDbContextFactory _systemFactory = systemFactory;
    private readonly IClientReleaseCatalogService _catalog = catalog;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly ISecurityService _securityService = securityService;
    private readonly ILogger<ClientReleaseAdminService> _logger = logger;

    public async Task<Result<IReadOnlyList<ClientReleaseOverviewDTO>>> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_tenantContext.IsPlatformAdmin)
            {
                return Result<IReadOnlyList<ClientReleaseOverviewDTO>>.Fail(NotAllowed);
            }

            Result<IReadOnlyList<ClientRelease>> releases = await _catalog.GetReleasesAsync(forceRefresh, cancellationToken);

            if (!releases.IsSuccess)
            {
                return Result<IReadOnlyList<ClientReleaseOverviewDTO>>.Fail(releases.Error ?? "Failed to load kiosk releases.");
            }

            // Deliberately unfiltered: a release reaches every company's PCs, so the platform admin
            // deciding whether to promote one needs the whole picture. Only counts leave this query.
            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync(cancellationToken);

            Dictionary<string, int> counts = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.ReportedClientVersion != null)
                .GroupBy(x => x.ReportedClientVersion!)
                .Select(x => new { Version = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.Version, x => x.Count, cancellationToken);

            List<ClientReleaseOverviewDTO> overview = releases.Data!
                .Select(x => new ClientReleaseOverviewDTO
                {
                    Release = x,
                    IsStable = ClientTargetVersionResolver.IsStable(x),
                    ReportedPcCount = counts.GetValueOrDefault(x.Version)
                })
                .ToList();

            return Result<IReadOnlyList<ClientReleaseOverviewDTO>>.Ok(overview);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ClientReleaseOverviewDTO>>.Fail($"Failed to load kiosk releases: {ex.Message}");
        }
    }

    public Task<Result<bool>> PromoteAsync(string version, CancellationToken cancellationToken = default) =>
        ChangeReleaseAsync(version, "promote", (release, userId) =>
        {
            if (release.WithdrawnAt != null)
            {
                return "A withdrawn release can't be promoted. Restore it first.";
            }

            if (ClientTargetVersionResolver.IsStable(release))
            {
                return $"v{release.Version} is already stable.";
            }

            release.PromotedToStableAt = DateTime.UtcNow;
            release.PromotedByUserId = userId;

            _logger.LogInformation("Kiosk release {Version} promoted to stable by {UserId}", release.Version, userId);

            return null;
        }, cancellationToken);

    public Task<Result<bool>> WithdrawAsync(string version, string reason, CancellationToken cancellationToken = default) =>
        ChangeReleaseAsync(version, "withdraw", (release, userId) =>
        {
            string trimmedReason = (reason ?? string.Empty).Trim();

            if (trimmedReason.Length == 0)
            {
                return "Say why the release is being withdrawn, so the next person knows.";
            }

            if (trimmedReason.Length > MaxWithdrawReasonLength)
            {
                return $"Keep the reason under {MaxWithdrawReasonLength} characters.";
            }

            if (release.WithdrawnAt != null)
            {
                return $"v{release.Version} is already withdrawn.";
            }

            release.WithdrawnAt = DateTime.UtcNow;
            release.WithdrawnByUserId = userId;
            release.WithdrawnReason = trimmedReason;

            _logger.LogWarning("Kiosk release {Version} withdrawn by {UserId}: {Reason}", release.Version, userId, trimmedReason);

            return null;
        }, cancellationToken);

    public Task<Result<bool>> RestoreAsync(string version, CancellationToken cancellationToken = default) =>
        ChangeReleaseAsync(version, "restore", (release, userId) =>
        {
            if (release.WithdrawnAt == null)
            {
                return $"v{release.Version} isn't withdrawn.";
            }

            release.WithdrawnAt = null;
            release.WithdrawnByUserId = null;
            release.WithdrawnReason = null;

            _logger.LogInformation("Kiosk release {Version} restored by {UserId}", release.Version, userId);

            return null;
        }, cancellationToken);

    // Shared shell: permission check, load, apply the change (which returns an error message or
    // null), save.
    private async Task<Result<bool>> ChangeReleaseAsync(string version, string action, Func<ClientRelease, string, string?> change, CancellationToken cancellationToken)
    {
        try
        {
            if (!_tenantContext.IsPlatformAdmin)
            {
                _logger.LogWarning("Refused to {Action} kiosk release {Version}: caller isn't a platform admin", action, version);
                return Result<bool>.Fail(NotAllowed);
            }

            Result<string> userId = await _securityService.GetCurrentUserIdAsync();

            if (!userId.IsSuccess || string.IsNullOrEmpty(userId.Data))
            {
                return Result<bool>.Fail("Couldn't work out who is signed in.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            ClientRelease? release = await ctx.ClientReleases
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Version == version, cancellationToken);

            if (release is null)
            {
                return Result<bool>.Fail($"Kiosk release v{version} wasn't found.");
            }

            string? error = change(release, userId.Data);

            if (error is not null)
            {
                return Result<bool>.Fail(error);
            }

            await ctx.SaveChangesAsync(cancellationToken);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Action} kiosk release {Version}", action, version);
            return Result<bool>.Fail($"Failed to {action} v{version}: {ex.Message}");
        }
    }
}
