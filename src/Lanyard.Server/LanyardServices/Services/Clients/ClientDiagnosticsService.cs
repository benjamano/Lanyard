using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Diagnostics;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR;
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
    IConfiguration configuration,
    ILogger<ClientDiagnosticsService> logger) : IClientDiagnosticsService
{
    public const int LogTailLines = 500;

    // Under the hub's 256 KB MaximumReceiveMessageSize with room for the SignalR envelope. The
    // kiosk enforces the same cap itself, since it is the side that would trip the limit.
    public const int LogTailMaxBytes = 200 * 1024;

    private static readonly TimeSpan LogTailTimeout = TimeSpan.FromSeconds(10);

    private readonly IClientService _clientService = clientService;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly IClientDiagnosticsStore _store = store;
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

    public Result<ClientLogTailCapture?> GetLatestLogTail(Guid clientId)
    {
        try
        {
            return Result<ClientLogTailCapture?>.Ok(_store.GetLatestLogTail(clientId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading the latest log tail for client {ClientId}", clientId);
            return Result<ClientLogTailCapture?>.Fail(ex.Message);
        }
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
