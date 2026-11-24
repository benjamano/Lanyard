using Lanyard.Shared.Kiosk;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace Lanyard.Client.Identify;

/// <summary>
/// The overlay one screen shows while staff identify the kiosk's displays: its number, device name,
/// resolution and program. A window of its own, so the projection window underneath is never
/// touched and its program carries on.
/// </summary>
public partial class DisplayIdentifyOverlayWindow : Window
{
    private const int GwlExStyle = -20;

    // Never activated (focus stays with the projection), clicks pass straight through, and kept
    // off Alt-Tab and the taskbar. WS_EX_LAYERED comes from AllowsTransparency.
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;

    public DisplayIdentifyOverlayWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void ShowScreen(int index, Screen screen, string? programName)
    {
        NumberText.Text = DisplayIdentifyRules.ScreenNumber(index).ToString();
        TitleText.Text = DisplayIdentifyRules.ScreenTitle(index);
        DetailText.Text = DisplayIdentifyRules.Describe(screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height, screen.Primary);
        ProgramText.Text = DisplayIdentifyRules.ProgramLine(programName);

        // Placed exactly as ProjectionWindow.ApplyLayout places a full-screen program, so the
        // overlay covers the same area the program does whatever the screen's scaling.
        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
    }

    private void MakeClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;

        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();

        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExTransparent | WsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
