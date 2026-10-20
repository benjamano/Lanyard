using System.Security.Claims;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

public sealed record KioskCredentialSummary(
    Guid Id,
    ClientCredentialSource Source,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    bool IsActive,
    DateTime? RevokedUtc,
    string? RevokeReason);

// What the Security tab shows for one kiosk.
public sealed record KioskSecuritySummary(
    ClientAuthMethod? LastAuthMethod,
    bool IsConnected,
    KioskCredentialSummary? Current,
    IReadOnlyList<KioskCredentialSummary> History);

public interface IKioskSecurityService
{
    Task<Result<KioskSecuritySummary>> GetAsync(Guid clientId);

    // Gives the (connected) kiosk a new credential and retires the old one once it has saved it.
    Task<Result<bool>> RotateAsync(Guid clientId);

    // Revokes the kiosk's credential and cuts off its live connection. It has to be paired again.
    Task<Result<bool>> RevokeAsync(Guid clientId, string? reason);
}

// The Admin side of a kiosk's credential: see it, rotate it, revoke it. Admin only, and only for
// the Admin's own company's kiosks - the client lookups go through the tenant-filtered factory, so
// another company's kiosk just isn't found.
public class KioskSecurityService(
    IDbContextFactory<ApplicationDbContext> factory,
    IKioskCredentialService credentialService,
    IClientService clientService,
    KioskConnectionRegistry connectionRegistry,
    IHubContext<SignalRControlHub> hubContext,
    AuthenticationStateProvider authenticationStateProvider,
    ILogger<KioskSecurityService> logger) : IKioskSecurityService
{
    // How long the kiosk has to save its new credential and say so.
    public static TimeSpan RotationAcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private const int HistoryLength = 10;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IKioskCredentialService _credentialService = credentialService;
    private readonly IClientService _clientService = clientService;
    private readonly KioskConnectionRegistry _connectionRegistry = connectionRegistry;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly AuthenticationStateProvider _authenticationStateProvider = authenticationStateProvider;
    private readonly ILogger<KioskSecurityService> _logger = logger;

    public async Task<Result<KioskSecuritySummary>> GetAsync(Guid clientId)
    {
        try
        {
            Result<string> admin = await GetAdminUserIdAsync();

            if (!admin.IsSuccess)
            {
                return Result<KioskSecuritySummary>.Fail(admin.Error!);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == clientId);

            if (client is null)
            {
                return Result<KioskSecuritySummary>.Fail(NotFoundError);
            }

            List<KioskCredentialSummary> history = await ctx.ClientCredentials
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.ClientId == clientId && x.ActivatedUtc != null)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(HistoryLength)
                .Select(x => new KioskCredentialSummary(x.Id, x.Source, x.CreatedUtc, x.LastUsedUtc, x.IsActive, x.RevokedUtc, x.RevokeReason))
                .ToListAsync();

            Result<bool> connected = await _clientService.IsClientConnectedAsync(clientId);

            return Result<KioskSecuritySummary>.Ok(new KioskSecuritySummary(
                client.LastAuthMethod,
                connected.IsSuccess && connected.Data,
                history.FirstOrDefault(x => x.IsActive),
                history));
        }
        catch (Exception ex)
        {
            return Result<KioskSecuritySummary>.Fail($"Failed to load the kiosk's sign-in details: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RotateAsync(Guid clientId)
    {
        try
        {
            Result<string> admin = await GetAdminUserIdAsync();

            if (!admin.IsSuccess)
            {
                return Result<bool>.Fail(admin.Error!);
            }

            Result<Client?> client = await _clientService.GetClientFromIdAsync(clientId);

            if (!client.IsSuccess || client.Data is null)
            {
                return Result<bool>.Fail(NotFoundError);
            }

            Result<bool> connected = await _clientService.IsClientConnectedAsync(clientId);
            string? connectionId = client.Data.MostRecentConnectionId;

            if (!connected.IsSuccess || !connected.Data || string.IsNullOrEmpty(connectionId))
            {
                return Result<bool>.Fail("The kiosk isn't connected, so it can't be given a new credential. Revoke it instead if it shouldn't connect again.");
            }

            Result<IssuedKioskCredential> pending = await _credentialService.BeginRotationAsync(clientId, admin.Data!);

            if (!pending.IsSuccess || pending.Data is null)
            {
                return Result<bool>.Fail(pending.Error!);
            }

            bool saved;

            using (CancellationTokenSource timeout = new(RotationAcknowledgementTimeout))
            {
                try
                {
                    saved = await _hubContext.Clients.Client(connectionId).InvokeAsync<bool>(ClientHubEvents.ReceiveCredential, pending.Data.RawToken, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    await _credentialService.CancelRotationAsync(pending.Data.CredentialId, "The kiosk didn't confirm in time");
                    _logger.LogWarning("Kiosk {ClientId} didn't confirm its new credential in time; rotation called off", clientId);

                    return Result<bool>.Fail("The kiosk didn't confirm in time, so it keeps its current credential. Try again.");
                }
                catch (HubException ex)
                {
                    // No ReceiveCredential handler: kiosk software older than 1.0.43.
                    await _credentialService.CancelRotationAsync(pending.Data.CredentialId, "The kiosk can't take a new credential");
                    _logger.LogWarning("Kiosk {ClientId} couldn't take a new credential: {Error}", clientId, ex.Message);

                    return Result<bool>.Fail("This kiosk's software is too old to change its credential. Update it, or revoke it and pair it again.");
                }
            }

            if (!saved)
            {
                await _credentialService.CancelRotationAsync(pending.Data.CredentialId, "The kiosk couldn't save it");

                return Result<bool>.Fail("The kiosk couldn't save its new credential, so it keeps its current one.");
            }

            Result<bool> completed = await _credentialService.CompleteRotationAsync(pending.Data.CredentialId, admin.Data!);

            if (!completed.IsSuccess)
            {
                return Result<bool>.Fail(completed.Error!);
            }

            // The live connection was made with the old credential; it reconnects at once on the new one.
            _connectionRegistry.Abort(clientId);

            _logger.LogInformation("Kiosk {ClientId}'s credential rotated by {UserId}", clientId, admin.Data);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to rotate the kiosk's credential: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RevokeAsync(Guid clientId, string? reason)
    {
        try
        {
            Result<string> admin = await GetAdminUserIdAsync();

            if (!admin.IsSuccess)
            {
                return Result<bool>.Fail(admin.Error!);
            }

            Result<Client?> client = await _clientService.GetClientFromIdAsync(clientId);

            if (!client.IsSuccess || client.Data is null)
            {
                return Result<bool>.Fail(NotFoundError);
            }

            string why = string.IsNullOrWhiteSpace(reason) ? "Revoked by an Admin" : reason.Trim();

            Result<int> revoked = await _credentialService.RevokeAllAsync(clientId, admin.Data!, why);

            if (!revoked.IsSuccess)
            {
                return Result<bool>.Fail(revoked.Error!);
            }

            int aborted = _connectionRegistry.Abort(clientId);

            _logger.LogInformation("Kiosk {ClientId} revoked by {UserId}: {Count} credential(s), {Aborted} live connection(s) closed", clientId, admin.Data, revoked.Data, aborted);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to revoke the kiosk: {ex.Message}");
        }
    }

    private const string NotFoundError = "Kiosk not found.";

    private async Task<Result<string>> GetAdminUserIdAsync()
    {
        ClaimsPrincipal user = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;
        string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        return user.Identity?.IsAuthenticated == true && userId is not null && user.IsInRole("Admin")
            ? Result<string>.Ok(userId)
            : Result<string>.Fail("Only Admins can change how a kiosk signs in.");
    }
}
