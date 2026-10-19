using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// The hardware self-test an Admin runs on a kiosk from the Clients page. Follows
/// <see cref="ClientDiagnosticsService"/>: only a kiosk that is connected right now, through a
/// SignalR client result. Unlike log tails, the results hold no personal data, so they are stored
/// (and purged by <see cref="KioskSelfTestRetentionHostedService"/>).
/// </summary>
/// <remarks>
/// The page is Admin-only, but this checks again rather than trusting it: a caller with no signed-in
/// user runs as the unfiltered system tenant, which would reach every company's kiosks. The kiosk is
/// only ever resolved through the tenant-filtered <see cref="IClientService.GetClientFromIdAsync"/>,
/// never the connection-id cache, which isn't filtered.
/// </remarks>
public class KioskSelfTestService(
    IClientService clientService,
    IHubContext<SignalRControlHub> hubContext,
    IClientDiagnosticsStore store,
    IDbContextFactory<ApplicationDbContext> factory,
    ITenantContext tenant,
    ISecurityService securityService,
    TimeProvider timeProvider,
    ILogger<KioskSelfTestService> logger) : IKioskSelfTestService
{
    // The kiosk gives each group of checks at most 8s and runs them side by side.
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(20);

    public const int MaxHistory = 50;

    private const string NotAllowed = "Only an Admin can run a kiosk self-test.";
    private const string NotFound = "Couldn't find this kiosk.";

    private readonly IClientService _clientService = clientService;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly IClientDiagnosticsStore _store = store;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ITenantContext _tenant = tenant;
    private readonly ISecurityService _securityService = securityService;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskSelfTestService> _logger = logger;

    /// <summary>How long to wait for the kiosk's checklist. Settable so tests needn't wait 20 seconds.</summary>
    public TimeSpan ReplyTimeout { get; set; } = DefaultReplyTimeout;

    public async Task<Result<KioskSelfTestRunView>> RunAsync(Guid clientId, bool flashDmx)
    {
        try
        {
            if (!await IsSignedInAdminAsync())
            {
                return Result<KioskSelfTestRunView>.Fail(NotAllowed);
            }

            Result<string> userIdResult = await _securityService.GetCurrentUserIdAsync();

            if (!userIdResult.IsSuccess)
            {
                return Result<KioskSelfTestRunView>.Fail(NotAllowed);
            }

            Result<Client?> clientResult = await _clientService.GetClientFromIdAsync(clientId);

            if (!clientResult.IsSuccess || clientResult.Data == null)
            {
                return Result<KioskSelfTestRunView>.Fail(NotFound);
            }

            Client client = clientResult.Data;

            Result<bool> connectedResult = await _clientService.IsClientConnectedAsync(clientId);

            if (!connectedResult.IsSuccess)
            {
                return Result<KioskSelfTestRunView>.Fail(connectedResult.Error!);
            }

            // MostRecentConnectionId outlives the connection, so an invoke on it would just time out.
            if (!connectedResult.Data || string.IsNullOrEmpty(client.MostRecentConnectionId))
            {
                return Result<KioskSelfTestRunView>.Fail("This kiosk isn't connected right now.");
            }

            if (!_store.TryBeginSelfTest(clientId, out string? busyReason))
            {
                return Result<KioskSelfTestRunView>.Fail(busyReason!);
            }

            try
            {
                _logger.LogInformation("User {UserId} started a self-test on client {ClientId} (flash DMX: {FlashDmx})", userIdResult.Data, clientId, flashDmx);

                ClientSelfTestRun run = new()
                {
                    Id = Guid.NewGuid(),
                    CompanyId = client.CompanyId,
                    ClientId = clientId,
                    RequestedByUserId = userIdResult.Data,
                    RequestedAtUtc = Now(),
                    FlashRequested = flashDmx
                };

                await AskKioskAsync(client.MostRecentConnectionId, run);

                await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

                ctx.ClientSelfTestRuns.Add(run);
                await ctx.SaveChangesAsync();

                _logger.LogInformation("Self-test {RunId} on client {ClientId} ended {Status} with {CheckCount} checks", run.Id, clientId, run.Status, run.Checks.Count);

                return await LoadRunAsync(run.Id) is KioskSelfTestRunView view
                    ? Result<KioskSelfTestRunView>.Ok(view)
                    : Result<KioskSelfTestRunView>.Fail("The self-test ran but its result couldn't be read back.");
            }
            finally
            {
                _store.EndSelfTest(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running a self-test on client {ClientId}", clientId);
            return Result<KioskSelfTestRunView>.Fail($"Failed to run the self-test: {ex.Message}");
        }
    }

    public async Task<Result<List<KioskSelfTestRunView>>> GetHistoryAsync(Guid clientId, int take = 10)
    {
        try
        {
            if (!await IsSignedInAdminAsync())
            {
                return Result<List<KioskSelfTestRunView>>.Fail(NotAllowed);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Tenant-filtered: another company's kiosk simply has no runs.
            List<ClientSelfTestRun> runs = await ctx.ClientSelfTestRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Checks)
                .Include(x => x.RequestedByUser)
                .Where(x => x.ClientId == clientId)
                .OrderByDescending(x => x.RequestedAtUtc)
                .Take(Math.Clamp(take, 1, MaxHistory))
                .ToListAsync();

            return Result<List<KioskSelfTestRunView>>.Ok(runs.Select(ToView).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading self-test history for client {ClientId}", clientId);
            return Result<List<KioskSelfTestRunView>>.Fail($"Failed to load the self-test history: {ex.Message}");
        }
    }

    /// <summary>Sends the request and fills in the run from the reply, or from why there wasn't one.</summary>
    private async Task AskKioskAsync(string connectionId, ClientSelfTestRun run)
    {
        KioskSelfTestRequestDTO request = new()
        {
            RunId = run.Id,
            FlashDmx = run.FlashRequested
        };

        KioskSelfTestResultDTO? reply;

        using (CancellationTokenSource timeout = new(ReplyTimeout))
        {
            try
            {
                reply = await _hubContext.Clients.Client(connectionId)
                    .InvokeAsync<KioskSelfTestResultDTO>(ClientHubMethods.RunSelfTest, request, timeout.Token);
            }
            // When our token cancels a client result, SignalR fails the call with a HubException
            // ("Invocation canceled by the server"), not an OperationCanceledException, so it has
            // to be told apart from a kiosk with no handler by the token, not the exception type.
            catch (Exception ex) when (ex is OperationCanceledException || (ex is HubException && timeout.IsCancellationRequested))
            {
                _logger.LogWarning("Client {ClientId} did not finish self-test {RunId} in time", run.ClientId, run.Id);
                Finish(run, ClientSelfTestStatus.TimedOut, $"The kiosk didn't reply within {ReplyTimeout.TotalSeconds:0} seconds. It may be frozen; try restarting it.");
                return;
            }
            catch (HubException ex)
            {
                // Raised when the kiosk has no RunSelfTest handler, i.e. a client from before it.
                _logger.LogWarning("Client {ClientId} could not run self-test {RunId}: {Error}", run.ClientId, run.Id, ex.Message);
                Finish(run, ClientSelfTestStatus.KioskTooOld, "This kiosk runs an older Lanyard Client without the self-test. Use Restart, then Restart Lanyard Client, to update it and try again.");
                return;
            }
        }

        string? problem = ValidateReply(reply, run.Id);

        if (problem != null)
        {
            _logger.LogWarning("Client {ClientId} sent an unusable reply to self-test {RunId}: {Problem}", run.ClientId, run.Id, problem);
            Finish(run, ClientSelfTestStatus.Failed, problem);
            return;
        }

        run.ClientVersion = SelfTestRules.Truncate(reply!.ClientVersion, ClientVersionFormat.MaxLength);
        run.Checks = reply.Checks
            .Select((check, index) => new ClientSelfTestCheck
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                SortOrder = index,
                Key = SelfTestRules.Truncate(check.Key, 50)!,
                // An outcome this server doesn't know is treated as a failure rather than hidden.
                Outcome = System.Enum.IsDefined(check.Outcome) ? check.Outcome : SelfTestOutcome.Fail,
                Code = SelfTestRules.Truncate(check.Code, 50),
                Detail = SelfTestRules.Truncate(check.Detail)
            })
            .ToList();

        Finish(run, ClientSelfTestStatus.Completed, null);
    }

    /// <summary>
    /// Why a reply can't be stored, or null when it can. The kiosk is trusted only so far: any
    /// holder of the shared kiosk secret can answer for a kiosk ID, so sizes are capped here too.
    /// </summary>
    private static string? ValidateReply(KioskSelfTestResultDTO? reply, Guid runId)
    {
        if (reply == null)
        {
            return "The kiosk sent back an empty reply.";
        }

        if (reply.RunId != runId)
        {
            return "The kiosk's reply was for a different self-test.";
        }

        if (reply.Checks == null || reply.Checks.Count == 0)
        {
            return "The kiosk didn't report any checks.";
        }

        if (reply.Checks.Count > SelfTestRules.MaxChecks)
        {
            return "The kiosk reported more checks than expected.";
        }

        if (reply.Checks.Any(x => string.IsNullOrWhiteSpace(x.Key)))
        {
            return "The kiosk reported a check without a name.";
        }

        return null;
    }

    private void Finish(ClientSelfTestRun run, ClientSelfTestStatus status, string? error)
    {
        run.Status = status;
        run.Error = error;
        run.CompletedAtUtc = Now();
    }

    private async Task<KioskSelfTestRunView?> LoadRunAsync(Guid runId)
    {
        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        ClientSelfTestRun? run = await ctx.ClientSelfTestRuns
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Checks)
            .Include(x => x.RequestedByUser)
            .FirstOrDefaultAsync(x => x.Id == runId);

        return run == null ? null : ToView(run);
    }

    private async Task<bool> IsSignedInAdminAsync()
    {
        // A system caller (no signed-in user) sees every company: never let one through.
        if (_tenant.IsSystem || _tenant.CompanyId == null)
        {
            return false;
        }

        return await _securityService.IsCurrentUserInRoleAsync("Admin");
    }

    public static KioskSelfTestRunView ToView(ClientSelfTestRun run)
    {
        List<KioskSelfTestGroupView> groups = run.Checks
            .OrderBy(x => x.SortOrder)
            .GroupBy(x => SelfTestCheckKeys.GroupOf(x.Key))
            .OrderBy(g => KioskSelfTestCatalogue.GroupOrder(g.Key))
            .Select(g => new KioskSelfTestGroupView
            {
                Group = g.Key,
                Title = KioskSelfTestCatalogue.GroupTitle(g.Key),
                Checks = g.Select(c => new KioskSelfTestCheckView
                {
                    Key = c.Key,
                    Title = KioskSelfTestCatalogue.Title(c.Key),
                    Outcome = c.Outcome,
                    Code = c.Code,
                    Detail = c.Detail,
                    Hint = KioskSelfTestCatalogue.Hint(c.Key, c.Outcome, c.Code)
                }).ToList()
            })
            .ToList();

        return new KioskSelfTestRunView
        {
            Id = run.Id,
            ClientId = run.ClientId,
            RequestedAtUtc = run.RequestedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            RequestedByName = DisplayName(run.RequestedByUser),
            Status = run.Status,
            Error = run.Error,
            ClientVersion = run.ClientVersion,
            FlashRequested = run.FlashRequested,
            Groups = groups
        };
    }

    private static string? DisplayName(UserProfile? user)
    {
        if (user == null)
        {
            return null;
        }

        string name = $"{user.FirstName} {user.LastName}".Trim();

        return name.Length > 0 ? name : user.Email ?? user.UserName;
    }

    private DateTime Now()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }
}
