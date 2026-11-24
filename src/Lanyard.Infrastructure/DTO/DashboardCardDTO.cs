using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO;

// One widget's type and place, enough to draw a dashboard's layout as a small thumbnail.
public sealed record DashboardCardWidget(WidgetType Type, int X, int Y, int W, int H);

// What the dashboards gallery shows for each dashboard, without loading every widget's settings.
public sealed record DashboardCard(
    Guid Id,
    string Name,
    string? Description,
    DateTime? LastUpdateDate,
    IReadOnlyList<DashboardCardWidget> Widgets,
    // Active projection programs with a Dashboard step showing this dashboard (how it reaches a kiosk TV).
    IReadOnlyList<string> ProjectionPrograms);
