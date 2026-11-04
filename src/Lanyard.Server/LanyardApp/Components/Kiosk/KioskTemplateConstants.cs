using Lanyard.Shared.Projection;

namespace Lanyard.App.Components.Kiosk;

// Kept as aliases so existing pages compile unchanged; the names themselves live in
// Lanyard.Shared so the kiosk's offline player and the server can't disagree on them.
public static class KioskTemplateConstants
{
    public static class TemplateNames
    {
        public const string ShowText = ProjectionTemplateNames.ShowText;
        public const string PlayVideo = ProjectionTemplateNames.PlayVideo;
        public const string Delay = ProjectionTemplateNames.Delay;
        public const string Dashboard = ProjectionTemplateNames.Dashboard;
        public const string LiveCapture = ProjectionTemplateNames.LiveCapture;
    }

    public static class ParameterNames
    {
        public const string Text = ProjectionParameterNames.Text;
        public const string Video = ProjectionParameterNames.Video;
        public const string ShouldRepeat = ProjectionParameterNames.ShouldRepeat;
        public const string Volume = ProjectionParameterNames.Volume;
        public const string Dashboard = ProjectionParameterNames.Dashboard;
        public const string VideoDevice = ProjectionParameterNames.VideoDevice;
        public const string EnableAudio = ProjectionParameterNames.EnableAudio;
    }
}
