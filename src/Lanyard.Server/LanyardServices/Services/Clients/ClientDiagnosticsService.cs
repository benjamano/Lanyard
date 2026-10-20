using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Diagnostics;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Remote diagnostics for kiosk clients, requested by an Admin from the Clients page. Follows
/// <see cref="ClientService.RestartClientAsync"/>: it only talks to a client that is connected
/// right now, and it waits for a SignalR client result, so a kiosk too old to have the handler
/// comes back as a clear failure rather than a request that silently goes nowhere.
/// </summary>
public class ClientDiagnosticsService(
    IClientService clientService,
    IHubContext<SignalRControlHub> hubContext,
    IClientDiagnosticsStore store,
    IDbContextFactory<ApplicationDbContext> factory,
    IConfiguration configuration,
    ILogger<ClientDiagnosticsService> logger) : IClientDiagnosticsService
{
    public const int LogTailLines = 500;

    // Under the hub's 256 KB MaximumReceiveMessageSize with room for the SignalR envelope. The
    // kiosk enforces the same cap itself, since it is the side that would trip the limit.
    public const int LogTailMaxBytes = 200 * 1024;

    private static readonly TimeSpan LogTailTimeout = TimeSpan.FromSeconds(10);

    // Enough to recognise what a screen shows, and small enough for one hub message per screen.
    public const int ScreenshotMaxWidth = 1280;
    public const int ScreenshotMaxBytes = ScreenshotSizing.MaxBytesLimit;

    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);

    // Screens are captured one after another; past this, the rest are marked as not captured.
    private static readonly TimeSpan ScreenshotsBudget = TimeSpan.FromSeconds(30);

    private const string TooOldForScreenshots = "This kiosk runs an older Lanyard Client without remote screenshots. Use Restart, then Restart Lanyard Client, to update it and try again.";

    private readonly IClientService _clientService = clientService;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly IClientDiagnosticsStore _store = store;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<ClientDiagnosticsService> _logger = logger;

    public async Task<Result<ClientLogTailCapture>> RequestLogTailAsync(Guid clientId, string requestedBy)
    {
        try
        {
            Result<string> connectionResult = await GetConnectedConnectionIdAsync(clientId);

            if (!connectionResult.IsSuccess)
            {
                return Result<ClientLogTailCapture>.Fail(connectionResult.Error!);
            }

            if (!_store.TryBeginLogTail(clientId, out string? busyReason))
            {
                return Result<ClientLogTailCapture>.Fail(busyReason!);
            }

            try
            {
                _logger.LogInformation("{RequestedBy} requested the log tail from client {ClientId}", requestedBy, clientId);

                ClientLogTailRequestDTO request = new()
                {
                    MaxLines = LogTailLines,
                    MaxBytes = LogTailMaxBytes
                };

                ClientLogTailDTO? logTail;

                using (CancellationTokenSource timeout = new(LogTailTimeout))
                {
                    try
                    {
                        logTail = await _hubContext.Clients.Client(connectionResult.Data!)
                            .InvokeAsync<ClientLogTailDTO>(ClientHubMethods.RequestLogTail, request, timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Client {ClientId} did not return its log tail in time", clientId);
                        return Result<ClientLogTailCapture>.Fail("The kiosk didn't respond within 10 seconds. It may be frozen; try restarting it.");
                    }
                    catch (HubException ex)
                    {
                        // Raised when the kiosk has no RequestLogTail handler to return a result,
                        // i.e. it's running a client version from before remote diagnostics.
                        _logger.LogWarning("Client {ClientId} could not return its log tail: {Error}", clientId, ex.Message);
                        return Result<ClientLogTailCapture>.Fail("This kiosk runs an older Lanyard Client without remote diagnostics. Use Restart, then Restart Lanyard Client, to update it and try again.");
                    }
                }

                if (logTail == null)
                {
                    _logger.LogWarning("Client {ClientId} returned an empty log tail", clientId);
                    return Result<ClientLogTailCapture>.Fail("The kiosk sent back an empty reply.");
                }

                RedactAgain(logTail);

                ClientLogTailCapture capture = _store.SaveLogTail(clientId, logTail, requestedBy);

                _logger.LogInformation("Client {ClientId} returned {LineCount} log lines for {RequestedBy}", clientId, logTail.Lines.Count, requestedBy);

                return Result<ClientLogTailCapture>.Ok(capture);
            }
            finally
            {
                _store.EndLogTail(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting the log tail from client {ClientId}", clientId);
            return Result<ClientLogTailCapture>.Fail($"Failed to fetch the log: {ex.Message}");
        }
    }

    public async Task<Result<ClientLogTailCapture?>> GetLatestLogTailAsync(Guid clientId)
    {
        try
        {
            // The store is shared by every company, so only hand back a capture for a kiosk the
            // caller can see through the tenant filter.
            if (!await IsVisibleAsync(clientId))
            {
                return Result<ClientLogTailCapture?>.Fail("Couldn't find this kiosk.");
            }

            return Result<ClientLogTailCapture?>.Ok(_store.GetLatestLogTail(clientId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading the latest log tail for client {ClientId}", clientId);
            return Result<ClientLogTailCapture?>.Fail(ex.Message);
        }
    }

    public async Task<Result<ClientScreenshotCapture>> RequestScreenshotsAsync(Guid clientId, string requestedBy)
    {
        try
        {
            Result<string> connectionResult = await GetConnectedConnectionIdAsync(clientId);

            if (!connectionResult.IsSuccess)
            {
                return Result<ClientScreenshotCapture>.Fail(connectionResult.Error!);
            }

            if (!_store.TryBeginScreenshots(clientId, out string? busyReason))
            {
                return Result<ClientScreenshotCapture>.Fail(busyReason!);
            }

            try
            {
                _logger.LogInformation("{RequestedBy} requested screenshots from client {ClientId}", requestedBy, clientId);

                ISingleClientProxy kiosk = _hubContext.Clients.Client(connectionResult.Data!);

                List<ClientProjectionWindowDTO>? windows;

                using (CancellationTokenSource timeout = new(ScreenshotTimeout))
                {
                    try
                    {
                        windows = await kiosk.InvokeAsync<List<ClientProjectionWindowDTO>>(ClientHubMethods.ListProjectionWindows, timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Client {ClientId} did not list its projection windows in time", clientId);
                        return Result<ClientScreenshotCapture>.Fail("The kiosk didn't respond within 10 seconds. It may be frozen; try restarting it.");
                    }
                    catch (HubException ex)
                    {
                        // Raised when the kiosk has no ListProjectionWindows handler, i.e. it runs
                        // a client version from before remote screenshots.
                        _logger.LogWarning("Client {ClientId} could not list its projection windows: {Error}", clientId, ex.Message);
                        return Result<ClientScreenshotCapture>.Fail(TooOldForScreenshots);
                    }
                }

                windows ??= [];

                Dictionary<Guid, string> programNames = await GetProgramNamesAsync(windows.Select(x => x.ProgramId));

                List<ClientScreenSnapshot> screens = [];
                string? clientVersion = null;

                using (CancellationTokenSource budget = new(ScreenshotsBudget))
                {
                    foreach (ClientProjectionWindowDTO window in windows)
                    {
                        (ClientScreenSnapshot screen, string? version) = await CaptureScreenAsync(kiosk, clientId, window, programNames, budget.Token);

                        screens.Add(screen);
                        clientVersion ??= version;
                    }
                }

                ClientScreenshotCapture capture = _store.SaveScreenshots(clientId, screens, clientVersion, requestedBy);

                _logger.LogInformation("Client {ClientId} returned {CapturedCount} of {ScreenCount} screenshots for {RequestedBy}",
                    clientId, screens.Count(x => x.ImageJpeg != null), screens.Count, requestedBy);

                return Result<ClientScreenshotCapture>.Ok(capture);
            }
            finally
            {
                _store.EndScreenshots(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting screenshots from client {ClientId}", clientId);
            return Result<ClientScreenshotCapture>.Fail($"Failed to capture the screens: {ex.Message}");
        }
    }

    public async Task<Result<ClientScreenshotCapture?>> GetLatestScreenshotsAsync(Guid clientId)
    {
        try
        {
            if (!await IsVisibleAsync(clientId))
            {
                return Result<ClientScreenshotCapture?>.Fail("Couldn't find this kiosk.");
            }

            return Result<ClientScreenshotCapture?>.Ok(_store.GetLatestScreenshots(clientId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading the latest screenshots for client {ClientId}", clientId);
            return Result<ClientScreenshotCapture?>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Captures one screen. A failure here only marks that screen, so one broken window doesn't
    /// lose the others.
    /// </summary>
    private async Task<(ClientScreenSnapshot Screen, string? ClientVersion)> CaptureScreenAsync(
        ISingleClientProxy kiosk,
        Guid clientId,
        ClientProjectionWindowDTO window,
        Dictionary<Guid, string> programNames,
        CancellationToken budget)
    {
        ClientScreenSnapshot Failed(string error) => new()
        {
            DisplayIndex = window.DisplayIndex,
            ProgramName = programNames.GetValueOrDefault(window.ProgramId),
            IsOffline = window.IsOffline,
            Error = error
        };

        if (!window.IsLoaded)
        {
            return (Failed("This screen's window failed to load, so there's nothing to capture."), null);
        }

        if (budget.IsCancellationRequested)
        {
            return (Failed("Not captured: the kiosk took too long with the other screens."), null);
        }

        ClientScreenshotRequestDTO request = new()
        {
            DisplayKey = window.DisplayKey,
            MaxWidth = ScreenshotMaxWidth,
            MaxBytes = ScreenshotMaxBytes
        };

        ClientScreenshotDTO? reply;

        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            timeout.CancelAfter(ScreenshotTimeout);

            try
            {
                reply = await kiosk.InvokeAsync<ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow, request, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Client {ClientId} did not capture display {DisplayIndex} in time", clientId, window.DisplayIndex);
                return (Failed("The kiosk didn't capture this screen in time."), null);
            }
            catch (HubException ex)
            {
                _logger.LogWarning("Client {ClientId} could not capture display {DisplayIndex}: {Error}", clientId, window.DisplayIndex, ex.Message);
                return (Failed("The kiosk couldn't capture this screen."), null);
            }
        }

        if (reply == null)
        {
            return (Failed("The kiosk sent back an empty reply."), null);
        }

        // Kept even when this screen failed: it is about the kiosk, not the screen.
        string? version = reply.ClientVersion;

        if (reply.ImageJpeg is not { Length: > 0 } || !IsJpeg(reply.ImageJpeg))
        {
            return (Failed(reply.Error ?? "The kiosk couldn't capture this screen."), version);
        }

        ClientScreenSnapshot screen = new()
        {
            DisplayIndex = window.DisplayIndex,
            ProgramName = programNames.GetValueOrDefault(window.ProgramId),
            IsOffline = window.IsOffline,
            ImageJpeg = reply.ImageJpeg,
            Width = reply.Width,
            Height = reply.Height
        };

        return (screen, version);
    }

    // The image is shown in a data: URL typed as JPEG, so anything else is refused rather than
    // passed to the browser as whatever it claims to be.
    private static bool IsJpeg(byte[] image)
    {
        return image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF;
    }

    /// <summary>Names for labelling each screen, read through the caller's tenant filter.</summary>
    private async Task<Dictionary<Guid, string>> GetProgramNamesAsync(IEnumerable<Guid> programIds)
    {
        List<Guid> ids = programIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        return await ctx.ProjectionPrograms
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name);
    }

    private async Task<bool> IsVisibleAsync(Guid clientId)
    {
        Result<Client?> clientResult = await _clientService.GetClientFromIdAsync(clientId);

        return clientResult.IsSuccess && clientResult.Data != null;
    }

    /// <summary>
    /// Resolves the connection to send to, failing when the client is offline: its
    /// MostRecentConnectionId outlives the connection, so an invoke on it would just time out.
    /// </summary>
    private async Task<Result<string>> GetConnectedConnectionIdAsync(Guid clientId)
    {
        Result<Client?> clientResult = await _clientService.GetClientFromIdAsync(clientId);

        if (!clientResult.IsSuccess || clientResult.Data == null)
        {
            return Result<string>.Fail("Couldn't find this kiosk.");
        }

        Result<bool> connectedResult = await _clientService.IsClientConnectedAsync(clientId);

        if (!connectedResult.IsSuccess)
        {
            return Result<string>.Fail(connectedResult.Error!);
        }

        string? connectionId = clientResult.Data.MostRecentConnectionId;

        if (!connectedResult.Data || string.IsNullOrEmpty(connectionId))
        {
            return Result<string>.Fail("This kiosk isn't connected right now.");
        }

        return Result<string>.Ok(connectionId);
    }

    /// <summary>
    /// The kiosk already redacts as it logs; this repeats it with the server's own copy of the
    /// shared secret, so a kiosk with a stale or missing secret setting still can't leak it.
    /// </summary>
    private void RedactAgain(ClientLogTailDTO logTail)
    {
        string?[] knownSecrets = [_configuration["Clients:SharedSecret"]];

        foreach (ClientLogLineDTO line in logTail.Lines)
        {
            line.Message = LogRedactor.Redact(line.Message, knownSecrets);
            line.Category = LogRedactor.Redact(line.Category, knownSecrets);
        }
    }
}
