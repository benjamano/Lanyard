using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// "Identify displays": an Admin presses a button and every screen on the kiosk shows its number,
/// device name, resolution and program for a few seconds, so screen numbers in Lanyard can be
/// matched to the physical screens. Follows <see cref="KioskSelfTestService"/>: a SignalR client
/// result to a kiosk that is connected right now. Nothing new is stored, except that the kiosk's
/// screen list is refreshed from the reply.
/// </summary>
/// <remarks>
/// The pages it is offered on are Admin-only, but this checks again rather than trusting them: a
/// caller with no signed-in user runs as the unfiltered system tenant. The kiosk is only resolved
/// through the tenant-filtered <see cref="IClientService.GetClientFromIdAsync"/>.
/// </remarks>
public class DisplayIdentifyService(
    IClientService clientService,
    IHubContext<SignalRControlHub> hubContext,
    IClientDiagnosticsStore store,
    IProjectionProgramRunnerService programRunner,
    ITenantContext tenant,
    ISecurityService securityService,
    ILogger<DisplayIdentifyService> logger) : IDisplayIdentifyService
{
    // The kiosk replies as soon as the overlays are up, which takes well under a second.
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(10);

    private const string NotAllowed = "Only an Admin can identify a kiosk's displays.";
    private const string NotFound = "Couldn't find this kiosk.";

    private readonly IClientService _clientService = clientService;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly IClientDiagnosticsStore _store = store;
    private readonly IProjectionProgramRunnerService _programRunner = programRunner;
    private readonly ITenantContext _tenant = tenant;
    private readonly ISecurityService _securityService = securityService;
    private readonly ILogger<DisplayIdentifyService> _logger = logger;

    /// <summary>How long to wait for the kiosk. Settable so tests needn't wait 10 seconds.</summary>
    public TimeSpan ReplyTimeout { get; set; } = DefaultReplyTimeout;

    public async Task<Result<DisplayIdentifyView>> IdentifyAsync(Guid clientId)
    {
        try
        {
            // A system caller (no signed-in user) sees every company: never let one through.
            if (_tenant.IsSystem || _tenant.CompanyId == null || !await _securityService.IsCurrentUserInRoleAsync("Admin"))
            {
                return Result<DisplayIdentifyView>.Fail(NotAllowed);
            }

            Result<Client?> clientResult = await _clientService.GetClientFromIdAsync(clientId);

            if (!clientResult.IsSuccess || clientResult.Data == null)
            {
                return Result<DisplayIdentifyView>.Fail(NotFound);
            }

            Client client = clientResult.Data;

            Result<bool> connectedResult = await _clientService.IsClientConnectedAsync(clientId);

            if (!connectedResult.IsSuccess)
            {
                return Result<DisplayIdentifyView>.Fail(connectedResult.Error!);
            }

            // MostRecentConnectionId outlives the connection, so an invoke on it would just time out.
            if (!connectedResult.Data || string.IsNullOrEmpty(client.MostRecentConnectionId))
            {
                return Result<DisplayIdentifyView>.Fail("This kiosk isn't connected right now.");
            }

            if (!_store.TryBeginIdentify(clientId, out string? busyReason))
            {
                return Result<DisplayIdentifyView>.Fail(busyReason!);
            }

            try
            {
                return await IdentifyConnectedAsync(client);
            }
            finally
            {
                _store.EndIdentify(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error identifying the displays on client {ClientId}", clientId);
            return Result<DisplayIdentifyView>.Fail($"Failed to identify the displays: {ex.Message}");
        }
    }

    private async Task<Result<DisplayIdentifyView>> IdentifyConnectedAsync(Client client)
    {
        Result<string> userIdResult = await _securityService.GetCurrentUserIdAsync();

        // Read before the reply refreshes them, to spot screens that changed number.
        Result<IEnumerable<ClientAvailableScreen>> storedResult = await _clientService.GetClientAvailableScreensAsync(client.Id);
        List<ClientAvailableScreen> stored = storedResult.IsSuccess ? storedResult.Data!.ToList() : [];

        Dictionary<int, string> programs = await GetProgramsByIndexAsync(client.Id);

        DisplayIdentifyRequestDTO request = new()
        {
            RequestId = Guid.NewGuid(),
            DurationSeconds = DisplayIdentifyRules.DefaultDurationSeconds,
            Labels = programs
                .Select(x => new DisplayIdentifyLabelDTO { DisplayIndex = x.Key, ProgramName = x.Value })
                .ToList()
        };

        _logger.LogInformation("User {UserId} is identifying the displays on client {ClientId} (request {RequestId})",
            userIdResult.Data, client.Id, request.RequestId);

        DisplayIdentifyResultDTO? reply;

        using (CancellationTokenSource timeout = new(ReplyTimeout))
        {
            try
            {
                reply = await _hubContext.Clients.Client(client.MostRecentConnectionId!)
                    .InvokeAsync<DisplayIdentifyResultDTO>(ClientHubMethods.IdentifyDisplays, request, timeout.Token);
            }
            // As in KioskSelfTestService: a cancelled client result fails with a HubException, so a
            // time-out is told apart from a kiosk with no handler by the token.
            catch (Exception ex) when (ex is OperationCanceledException || (ex is HubException && timeout.IsCancellationRequested))
            {
                _logger.LogWarning("Client {ClientId} did not reply to identify request {RequestId} in time", client.Id, request.RequestId);
                return Result<DisplayIdentifyView>.Fail($"The kiosk didn't reply within {ReplyTimeout.TotalSeconds:0} seconds. It may be frozen; try restarting it.");
            }
            catch (HubException ex)
            {
                // Raised when the kiosk has no IdentifyDisplays handler, i.e. a client from before it.
                _logger.LogWarning("Client {ClientId} could not identify its displays: {Error}", client.Id, ex.Message);
                return Result<DisplayIdentifyView>.Fail(TooOldMessage(client.ReportedClientVersion));
            }
        }

        string? problem = DisplayIdentifyRules.ValidateReply(reply, request.RequestId);

        if (problem != null)
        {
            _logger.LogWarning("Client {ClientId} sent an unusable reply to identify request {RequestId}: {Problem}", client.Id, request.RequestId, problem);
            return Result<DisplayIdentifyView>.Fail(problem);
        }

        List<IdentifiedDisplayView> displays = reply!.Displays
            .OrderBy(x => x.Index)
            .Select(x => ToView(x, stored, programs))
            .ToList();

        await RefreshStoredScreensAsync(client.Id, displays);

        _logger.LogInformation("Client {ClientId} identified {Count} displays ({Shown} overlays shown, {Changed} changed number)",
            client.Id, displays.Count, displays.Count(x => x.Shown), displays.Count(x => x.Changed));

        return Result<DisplayIdentifyView>.Ok(new DisplayIdentifyView
        {
            ClientId = client.Id,
            DurationSeconds = request.DurationSeconds,
            ClientVersion = DisplayIdentifyRules.Truncate(reply.ClientVersion, ClientVersionFormat.MaxLength),
            Displays = displays
        });
    }

    /// <summary>
    /// The program for each screen: whatever is playing there now, else what is set up for it.
    /// A temporary trigger or automation can be playing something other than the saved program.
    /// </summary>
    private async Task<Dictionary<int, string>> GetProgramsByIndexAsync(Guid clientId)
    {
        Dictionary<int, string> programs = [];

        Result<IEnumerable<ClientProjectionSettings>> settingsResult = await _clientService.GetClientProjectionSettingsAsync(clientId);

        if (settingsResult.IsSuccess)
        {
            foreach (ClientProjectionSettings setting in settingsResult.Data!.Where(x => x.IsActive && x.ProjectionProgram is { IsActive: true }))
            {
                programs.TryAdd(setting.DisplayIndex, setting.ProjectionProgram!.Name);
            }
        }
        else
        {
            _logger.LogWarning("Couldn't load the projection settings of client {ClientId} for identify: {Error}", clientId, settingsResult.Error);
        }

        foreach (ProjectionRunState running in _programRunner.GetRunningStates().Where(x => x.ClientId == clientId))
        {
            programs[running.DisplayIndex] = running.ProgramName;
        }

        return programs.ToDictionary(x => x.Key, x => DisplayIdentifyRules.Truncate(x.Value, DisplayIdentifyRules.MaxProgramNameLength)!);
    }

    private static IdentifiedDisplayView ToView(IdentifiedDisplayDTO display, List<ClientAvailableScreen> stored, Dictionary<int, string> programs)
    {
        string deviceName = DisplayIdentifyRules.Truncate(display.DeviceName, DisplayIdentifyRules.MaxDeviceNameLength) ?? string.Empty;

        ClientAvailableScreen? before = stored.FirstOrDefault(x => x.Index == display.Index);
        bool changed = before != null && !string.Equals(before.Name, deviceName, StringComparison.OrdinalIgnoreCase);

        return new IdentifiedDisplayView
        {
            Index = display.Index,
            Number = DisplayIdentifyRules.ScreenNumber(display.Index),
            DeviceName = deviceName,
            Width = display.Width,
            Height = display.Height,
            IsPrimary = display.IsPrimary,
            ProgramName = programs.GetValueOrDefault(display.Index),
            Shown = display.Shown,
            Error = DisplayIdentifyRules.Truncate(display.Error, DisplayIdentifyRules.MaxErrorLength),
            Changed = changed,
            PreviousDeviceName = changed ? before!.Name : null
        };
    }

    private async Task RefreshStoredScreensAsync(Guid clientId, List<IdentifiedDisplayView> displays)
    {
        Result<bool> result = await _clientService.SetClientAvailableScreensAsync(clientId, displays
            .Select(x => new ClientAvailableScreenDTO
            {
                ClientId = clientId,
                Name = x.DeviceName,
                Width = x.Width,
                Height = x.Height,
                Index = x.Index
            })
            .ToList());

        if (!result.IsSuccess)
        {
            // The overlays still showed, so this isn't worth failing the request over.
            _logger.LogWarning("Couldn't refresh the screen list of client {ClientId} after identify: {Error}", clientId, result.Error);
        }
    }

    private static string TooOldMessage(string? reportedVersion)
    {
        string runs = string.IsNullOrWhiteSpace(reportedVersion)
            ? "an older Lanyard Client"
            : $"Lanyard Client {reportedVersion}";

        return $"This kiosk runs {runs}, which can't identify its displays (that needs {DisplayIdentifyRules.MinimumClientVersion} or later). Use Restart, then Restart Lanyard Client, to update it and try again.";
    }
}
