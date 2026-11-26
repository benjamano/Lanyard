namespace Lanyard.App.Components.Kiosk;

// Which kiosk a dashboard is showing on, cascaded to its widgets by RenderDashboardTemplate. Kiosk
// pages run signed out; ViewerToken is the token the kiosk got over its own hub connection, which a
// widget passes back to the server as proof (see IChecklistRunService.CompleteOnKioskAsync).
// Absent anywhere else (a signed-in home screen or the dashboard editor).
public sealed record KioskViewContext(Guid ClientId, string? ViewerToken);
