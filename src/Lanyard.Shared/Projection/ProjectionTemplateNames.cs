namespace Lanyard.Shared.Projection;

/// <summary>
/// Projection step templates and parameters are matched by name. These are the one copy of
/// those names, shared by the kiosk page, the kiosk media endpoint, the offline manifest and
/// the kiosk's offline player.
/// </summary>
public static class ProjectionTemplateNames
{
    public const string ShowText = "Show Text";
    public const string PlayVideo = "Play a Video";
    public const string Delay = "Delay";
    public const string Dashboard = "Dashboard";
    public const string LiveCapture = "Live Capture";
}

public static class ProjectionParameterNames
{
    public const string Text = "Text";
    public const string Video = "Video File";
    public const string ShouldRepeat = "ShouldRepeat";
    public const string Volume = "Volume";
    public const string Dashboard = "Dashboard";
    public const string VideoDevice = "Video Device";
    public const string EnableAudio = "Enable Audio";
}
