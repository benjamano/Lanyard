using Lanyard.Client.UI;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public class ProjectionProgramsService(ILogger<ProjectionProgramsService> logger) : IProjectionProgramsService
{
    private readonly ILogger<ProjectionProgramsService> _logger = logger;

    private List<ClientProjectionSettingsDTO> loadedProjectionPrograms = [];

    private Process? _kioskProcess;

    public async Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms)
    {
        HideWindow();

        if (projectionPrograms == null || !projectionPrograms.Any())
        {
            return;
        }

        _logger.LogInformation("Found {programCount} programs to run", projectionPrograms.Count());

        loadedProjectionPrograms = [.. projectionPrograms];

        await StartProjectionLoopAsync();
    }

    private async Task StartProjectionLoopAsync()
    {
        foreach (ClientProjectionSettingsDTO projectionProgram in loadedProjectionPrograms)
        {
            await StartProjectionProgramAsync(projectionProgram);
        }
    }

    private async Task StartProjectionProgramAsync(ClientProjectionSettingsDTO projectionProgram)
    {
        _logger.LogInformation("Starting projection program: {program}", projectionProgram.ProjectionProgram.Name);
        _logger.LogInformation(" - Fullscreen: {isFullScreen}", projectionProgram.IsFullScreen);
        _logger.LogInformation(" - Borderless: {isBorderless}", projectionProgram.IsBorderless);
        _logger.LogInformation(" - Resolution: {width}x{height}", projectionProgram.Width, projectionProgram.Height);
        _logger.LogInformation(" - Display Index: {displayIndex}", projectionProgram.DisplayIndex);

        ShowWindow(projectionProgram.DisplayIndex, projectionProgram.Width, projectionProgram.Height, projectionProgram.IsFullScreen, projectionProgram.ProjectionProgram.Id, Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!));
    }

    private void HideWindow()
    {
        try
        {
            if (_kioskProcess != null)
            {
                _kioskProcess.Kill();
                _kioskProcess.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill kiosk process.");
        }
        finally
        {
            _kioskProcess?.Dispose();
            _kioskProcess = null;
        }
    }

    void ShowWindow(int displayIndex, int width, int height, bool isFullScreen, Guid projectionProgramId, Guid clientId)
    {
        Screen[] screens = Screen.AllScreens;

        if (displayIndex < 0 || displayIndex >= screens.Length)
            displayIndex = 0;

        Screen screen = screens[displayIndex];

        int x = screen.Bounds.Left;
        int y = screen.Bounds.Top;

        string url = $"{Environment.GetEnvironmentVariable("KIOSK_SERVER_URL")}/{clientId}/{projectionProgramId}";

        string userDataDir = Path.Combine(
            Path.GetTempPath(),
            "LanyardKiosk",
            projectionProgramId.ToString()
        );

        _logger.LogInformation("Opening page with URL: {url}", url);

        Directory.CreateDirectory(userDataDir);

        string args =
            $"--kiosk \"{url}\" " +
            $"--user-data-dir=\"{userDataDir}\" " +
            $"--window-position={x},{y} " +
            $"--window-size={width/2},{height/2} " +
            $"--no-first-run --disable-session-crashed-bubble " +
            (isFullScreen ? "--edge-kiosk-type=fullscreen " : "");

        _kioskProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
            Arguments = args,
            UseShellExecute = false
        });
    }
}