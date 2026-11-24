using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Lanyard.App.Components.Dashboards;

// A starting point for a new dashboard: a set of widgets already laid out. Built in code rather
// than stored, so they never drift from the widgets that exist.
public sealed record DashboardTemplate(
    string Key,
    string Name,
    string Description,
    Func<Icon> Icon,
    Func<List<DashboardWidget>> Build,
    bool FitToScreen = false,
    int RowHeight = Dashboard.DefaultRowHeight)
{
    public IReadOnlyList<DashboardCardWidget> Preview =>
        [.. Build().Select(w => new DashboardCardWidget(w.Type, w.GridX, w.GridY, w.GridW, w.GridH))];
}

public static class DashboardTemplates
{
    private static T At<T>(T widget, int x, int y, int w, int h) where T : DashboardWidget
    {
        widget.GridX = x;
        widget.GridY = y;
        widget.GridW = w;
        widget.GridH = h;
        return widget;
    }

    public static IReadOnlyList<DashboardTemplate> All { get; } =
    [
        new("blank", "Blank", "An empty dashboard to build from scratch.",
            () => new Icons.Regular.Size20.Grid(), () => []),

        new("staff-home", "Staff home screen", "A greeting, the time, and each person's own shifts, training and announcements.",
            () => new Icons.Regular.Size20.Home(), () =>
            [
                At(new GreetingWidget(), 0, 0, 8, 1),
                At(new DigitalClockWidget(), 8, 0, 4, 1),
                At(new MyShiftsWidget(), 0, 1, 4, 3),
                At(new MyTrainingWidget(), 4, 1, 4, 3),
                At(new AnnouncementsWidget(), 8, 1, 4, 3)
            ]),

        new("manager", "Manager's morning", "Who's on, what's on, time off to approve, alerts and compliance in one place.",
            () => new Icons.Regular.Size20.Briefcase(), () =>
            [
                At(new WhoIsOnTodayWidget(), 0, 0, 4, 4),
                At(new PendingTimeOffWidget(), 4, 0, 4, 3),
                At(new OperationsAlertsWidget(), 8, 0, 4, 3),
                At(new ComplianceSummaryWidget(), 4, 3, 4, 4),
                At(new WhatsOnTodayWidget(), 8, 3, 4, 4)
            ]),

        new("front-desk", "Front desk", "The time, prices and notices, who's on, and whether every kiosk is running.",
            () => new Icons.Regular.Size20.PeopleTeam(), () =>
            [
                At(new DigitalClockWidget(), 0, 0, 3, 1),
                At(new TextAreaWidget { Title = "Prices", ShowTitle = true, Content = "<p>Add your prices or notices here.</p>" }, 0, 1, 3, 3),
                At(new AnnouncementsWidget(), 3, 0, 5, 3),
                At(new WhoIsOnTodayWidget(), 8, 0, 4, 4),
                At(new KioskHealthWidget(), 3, 3, 5, 3)
            ]),

        new("scoreboard-tv", "Arena scoreboard TV", "Full-screen for a kiosk TV: the live game, the scores and today's best.",
            () => new Icons.Regular.Size20.Games(), () =>
            [
                At(new ClientZoneLaserGameStatusWidget(), 0, 0, 4, 2),
                At(new ClientZoneLaserScoreboardWidget(), 4, 0, 8, 4),
                At(new HallOfFameWidget(), 0, 2, 4, 4),
                At(new DigitalClockWidget(), 4, 4, 8, 2)
            ],
            FitToScreen: true)
    ];
}
