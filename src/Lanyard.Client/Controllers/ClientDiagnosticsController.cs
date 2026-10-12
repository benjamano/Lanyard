using Lanyard.Shared.Diagnostics;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Lanyard.Client.Controllers;

public class ClientDiagnosticsController(LogLineBuffer logBuffer, ILogger<ClientDiagnosticsController> logger)
{
    // The hub rejects any message over 256 KB, and the reply is JSON inside a SignalR envelope,
    // so the kiosk never answers with more than this however much the server asks for.
    private const int MaxReplyBytes = 200 * 1024;

    private readonly LogLineBuffer _logBuffer = logBuffer;
    private readonly ILogger<ClientDiagnosticsController> _logger = logger;

    public void Register(HubConnection connection)
    {
        // A client result: the server awaits the reply, and a kiosk too old to have this
        // handler comes back to it as a HubException rather than a silent no-op.
        connection.On<ClientLogTailRequestDTO, ClientLogTailDTO>(ClientHubMethods.RequestLogTail, request =>
        {
            int maxLines = Math.Clamp(request?.MaxLines ?? LogLineBuffer.DefaultCapacity, 1, _logBuffer.Capacity);
            int maxBytes = Math.Clamp(request?.MaxBytes ?? MaxReplyBytes, 1024, MaxReplyBytes);

            _logger.LogInformation("Sending the last {MaxLines} log lines to the server for remote diagnostics", maxLines);

            ClientLogTailDTO tail = _logBuffer.Snapshot(maxLines, maxBytes);
            tail.ClientVersion = GetClientVersion();
            tail.CapturedAtUtc = DateTime.UtcNow;

            return tail;
        });
    }

    private static string? GetClientVersion()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        // The SDK appends "+<commit sha>" to the informational version.
        return version?.Split('+')[0];
    }
}
