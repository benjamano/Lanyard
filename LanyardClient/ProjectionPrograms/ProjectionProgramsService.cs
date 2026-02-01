using Lanyard.Client.UI;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public class ProjectionProgramsService(ILogger<ProjectionProgramsService> logger) : IProjectionProgramsService
{
    private readonly ILogger<ProjectionProgramsService> _logger = logger;

    private List<ClientProjectionSettingsDTO> loadedProjectionPrograms = [];

    private Process? _kioskProcess;

    public async Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms)
    {
        if (projectionPrograms == null || !projectionPrograms.Any())
        {
            HideWindow();

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
        _kioskProcess?.Kill();
        _kioskProcess = null;
    }

    void ShowWindow(int displayIndex, int width, int height, bool IsFullScreen, Guid projectionProgramId, Guid clientId)
    {
        Screen[] screens = Screen.AllScreens;

        if (displayIndex < 0 || displayIndex >= screens.Length)
        {
            displayIndex = 0;
        }

        Screen? screen = screens[displayIndex];

        int x = screen.Bounds.Left;
        int y = screen.Bounds.Top;

        string url = $"{Environment.GetEnvironmentVariable("KIOSK_SERVER_URL")}/{clientId}/{projectionProgramId}";

        string args = $"--no-first-run --no-default-browser-check --new-window --kiosk \"{url}\" --window-position={x},{y} --window-size={width},{height} {(IsFullScreen ? "--edge-kiosk-type=fullscreen" : "")}";

        Process process = Process.Start("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", args);
        _kioskProcess = process;
    }
}