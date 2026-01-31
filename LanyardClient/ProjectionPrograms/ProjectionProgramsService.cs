using Lanyard.Client.UI;
using Lanyard.Shared.DTO;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public class ProjectionProgramsService(ILogger<ProjectionProgramsService> logger) : IProjectionProgramsService
{
    private readonly ILogger<ProjectionProgramsService> _logger = logger;

    private List<ClientProjectionSettingsDTO> loadedProjectionPrograms = [];

    private ProjectionPopupWindow? _currentWindow;

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

        ShowWindow(projectionProgram.DisplayIndex, projectionProgram.Width, projectionProgram.Height, projectionProgram.IsFullScreen, projectionProgram.IsBorderless);
    }

    private void HideWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_currentWindow != null)
            {
                _currentWindow.Hide();
                _currentWindow = null;
            }
        });
    }

    private void ShowWindow(int displayIndex, int width, int height, bool isFullScreen, bool isBorderless)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _currentWindow?.Hide();

            ProjectionPopupWindow window = new();

            Screen[] screens = Screen.AllScreens;

            if (displayIndex < 0 || displayIndex >= screens.Length)
            {
                displayIndex = 0;
            }

            Screen? screen = screens[displayIndex];

            window.Left = screen.Bounds.Left;
            window.Top = screen.Bounds.Top;
            window.Width = isFullScreen ? screen.Bounds.Width : width;
            window.Height = isFullScreen ? screen.Bounds.Height : height;

            if (isFullScreen)
            {
                window.WindowStyle = isBorderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
                window.ResizeMode = ResizeMode.NoResize;
                window.WindowState = WindowState.Maximized;
            }
            else
            {
                window.Width = width;
                window.Height = height;
                window.WindowStyle = isBorderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
                window.ResizeMode = isBorderless ? ResizeMode.NoResize : ResizeMode.CanResize;
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            _currentWindow = window;
        });
    }
}
