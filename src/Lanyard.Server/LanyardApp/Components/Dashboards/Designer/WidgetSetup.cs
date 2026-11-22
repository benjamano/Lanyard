using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Dashboards.Designer;

// Whether a widget still needs choosing something before it can show anything: a kiosk for a
// scoreboard, a rule for a rule status. The designer flags these so a freshly dropped widget
// doesn't just look broken.
public static class WidgetSetup
{
    public static bool NeedsSetup(DashboardWidget widget) => widget switch
    {
        ClientZoneLaserScoreboardWidget w => w.ClientId is null,
        ClientZoneLaserGameStatusWidget w => w.ClientId is null,
        MusicTimelineWidget w => w.ClientId is null,
        MusicPlaylistSelectorWidget w => w.ClientId is null,
        ProjectionStatusWidget w => w.ClientId is null,
        AutomationRuleStatusWidget w => w.AutomationRuleId is null,
        ButtonWidget w => w.ActionType is null or ButtonActionType.Unknown
            || w.ClientId is null
            || (w.ActionType == ButtonActionType.TriggerProjectionProgram && w.ProjectionProgramId is null),
        _ => false
    };
}
