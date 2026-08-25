using Microsoft.Web.WebView2.Core;
using System.Windows;
using System.Windows.Forms;

namespace Lanyard.Client.ProjectionPrograms;

public partial class ProjectionWindow : Window
{
    public ProjectionWindow()
    {
        InitializeComponent();

        Closed += (_, _) => WebView.Dispose();
    }

    // Mirrors the previous Edge-process window placement: positioned at the target monitor's
    // top-left corner; fullscreen takes over the full monitor bounds regardless of the
    // configured width/height (matching --edge-kiosk-type=fullscreen), otherwise the window is
    // sized/positioned in the corner of the target screen at the configured (halved) size.
    public void ApplyLayout(Screen screen, int width, int height, bool isFullScreen, bool isBorderless)
    {
        WindowStyle = isBorderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = isBorderless ? ResizeMode.NoResize : ResizeMode.CanResize;

        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;

        if (isFullScreen)
        {
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
        }
        else
        {
            // Preserved as-is from the previous Edge --window-size flag - the halving looks like
            // it might be unintentional, but changing it wasn't part of this change's scope.
            Width = width / 2;
            Height = height / 2;
        }
    }

    public async Task InitializeAsync(CoreWebView2Environment environment, string url)
    {
        await WebView.EnsureCoreWebView2Async(environment);

        WebView.CoreWebView2.PermissionRequested += OnPermissionRequested;
        WebView.CoreWebView2.WindowCloseRequested += OnWindowCloseRequested;

        WebView.CoreWebView2.Navigate(url);
    }

    // Each program gets a fresh user-data-dir (see CoreWebView2Environment creation), so
    // camera/mic permission grants never persist across programs; auto-accept is required for
    // Live Capture steps to start without a prompt.
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    // Fires when the kiosk page calls window.close() - closing the window here is what lets
    // TriggerTemporaryProjectionProgramAsync's completion signal fire, same as the previous
    // "await process exit" contract.
    private void OnWindowCloseRequested(object? sender, object e)
    {
        Close();
    }
}
