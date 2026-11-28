using Lanyard.Application.Services.Projection;
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
        public const string WaitForGame = GameWaitStepNames.Template;
    }

    /// <summary>
    /// Steps that only hold (Delay, Wait for Game) and draw nothing, so whatever the previous
    /// step put on screen stays there.
    /// </summary>
    public static bool IsHoldOnly(string? templateName) =>
        string.Equals(templateName, TemplateNames.Delay, StringComparison.Ordinal)
        || string.Equals(templateName, TemplateNames.WaitForGame, StringComparison.Ordinal);

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
