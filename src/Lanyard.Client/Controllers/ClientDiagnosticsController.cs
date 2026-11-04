using Lanyard.Client.ProjectionPrograms;
using Lanyard.Shared.Diagnostics;
using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Lanyard.Client.Controllers;

public class ClientDiagnosticsController(
    LogLineBuffer logBuffer,
    IProjectionProgramsService projectionProgramsService,
    ILogger<ClientDiagnosticsController> logger)
{
    // The hub rejects any message over 256 KB, and the reply is JSON inside a SignalR envelope,
    // so the kiosk never answers with more than this however much the server asks for.
    private const int MaxReplyBytes = 200 * 1024;

    private readonly LogLineBuffer _logBuffer = logBuffer;
    private readonly IProjectionProgramsService _projectionProgramsService = projectionProgramsService;
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

        connection.On<List<ClientProjectionWindowDTO>>(ClientHubMethods.ListProjectionWindows, async () =>
        {
            List<ClientProjectionWindowDTO> windows = await _projectionProgramsService.ListWindowsAsync();

            _logger.LogInformation("Listing {WindowCount} projection windows for remote screenshots", windows.Count);

            return windows;
        });

        connection.On<ClientScreenshotRequestDTO, ClientScreenshotDTO>(ClientHubMethods.CaptureProjectionWindow, CaptureAsync);
    }

    private async Task<ClientScreenshotDTO> CaptureAsync(ClientScreenshotRequestDTO request)
    {
        string displayKey = request?.DisplayKey ?? string.Empty;
        int maxWidth = ScreenshotSizing.ClampMaxWidth(request?.MaxWidth ?? 0);
        int maxBytes = ScreenshotSizing.ClampMaxBytes(request?.MaxBytes ?? 0);

        ClientScreenshotDTO reply = new()
        {
            DisplayKey = displayKey,
            ClientVersion = GetClientVersion()
        };

        try
        {
            _logger.LogInformation("Capturing the projection window on {DisplayKey} for a remote screenshot", displayKey);

            byte[]? png = await _projectionProgramsService.CaptureWindowPngAsync(displayKey);

            if (png == null)
            {
                reply.Error = "This screen's window isn't showing anything yet. It may have failed to load.";
                return reply;
            }

            // Re-encoding is CPU work, so keep it off the UI thread the capture ran on.
            (byte[] jpeg, int width, int height) = await Task.Run(() => ScreenshotEncoder.Encode(png, maxWidth, maxBytes));

            if (jpeg.Length > maxBytes)
            {
                reply.Error = "The screenshot was too large to send.";
                return reply;
            }

            reply.ImageJpeg = jpeg;
            reply.Width = width;
            reply.Height = height;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture the projection window on {DisplayKey}", displayKey);
            reply.Error = "The kiosk couldn't capture this screen.";
        }
        finally
        {
            reply.CapturedAtUtc = DateTime.UtcNow;
        }

        return reply;
    }

    internal static string? GetClientVersion()
    {
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        // The SDK appends "+<commit sha>" to the informational version.
        return version?.Split('+')[0];
    }
}
