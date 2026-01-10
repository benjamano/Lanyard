using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.UI;

public class WindowManager(ILogger<WindowManager> logger)
{
    private readonly ILogger<WindowManager> _logger = logger;
    private TestWindow? _helloWorldWindow;

    public void ShowHelloWorldWindow(int displayNumber = 0)
    {
        if (_helloWorldWindow != null)
        {
            _logger.LogWarning("Hello World window is already open.");
            return;
        }

        _helloWorldWindow = new TestWindow();

        PositionWindowOnDisplay(_helloWorldWindow, displayNumber);

        _helloWorldWindow.Closed += (s, e) => _helloWorldWindow = null;
        _helloWorldWindow.Show();

        _logger.LogInformation("Hello World window opened on display {DisplayNumber}", displayNumber);
    }

    public void CloseHelloWorldWindow()
    {
        _helloWorldWindow?.Close();
        _helloWorldWindow = null;
    }

    private void PositionWindowOnDisplay(Window window, int displayNumber)
    {
        Screen[] screens = Screen.AllScreens;

        if (displayNumber >= screens.Length)
        {
            _logger.LogWarning("Display {DisplayNumber} not found. Using primary display.", displayNumber);
            displayNumber = 0;
        }

        Screen targetScreen = screens[displayNumber];

        window.Left = targetScreen.Bounds.Left;
        window.Top = targetScreen.Bounds.Top;
        window.Width = targetScreen.Bounds.Width;
        window.Height = targetScreen.Bounds.Height;

        _logger.LogInformation("Positioned window on display {DisplayNumber} at ({Left}, {Top}) with size {Width}x{Height}",
            displayNumber, window.Left, window.Top, window.Width, window.Height);
    }
}