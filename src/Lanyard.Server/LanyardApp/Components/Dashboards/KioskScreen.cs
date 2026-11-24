namespace Lanyard.App.Components.Dashboards;

// Cascaded to a dashboard's widgets when it's shown on a kiosk, so a widget can tell it's on one
// and act as that kiosk (the viewer token proves to the server that it really is the kiosk
// window its client opened). Absent on home screens and in the designer.
public sealed record KioskScreen(Guid ClientId, string? ViewerToken);
