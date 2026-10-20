namespace Lanyard.Shared.Kiosk;

// Small URL rules the kiosk's browser windows follow (Security Issue 9.10). Kept here rather than
// in the WPF client so they can be unit tested.
public static class KioskUrls
{
    // For logs: a kiosk page or publisher URL carries a token in its query string, and the client's
    // logs go to OpenTelemetry as well as the screen.
    public static string WithoutQuery(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        int query = url.IndexOfAny(['?', '#']);

        return query < 0 ? url : url[..query];
    }

    // Camera and microphone are only granted to the page the window was opened on, not to anything
    // it navigates or is redirected to.
    public static bool IsSameOrigin(string? requestUri, string? windowUrl) =>
        Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? request)
        && Uri.TryCreate(windowUrl, UriKind.Absolute, out Uri? window)
        && string.Equals(request.GetLeftPart(UriPartial.Authority), window.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    // The one opt-in for accepting an untrusted server certificate (a dev server on the .NET dev
    // cert), shared by the hub connection, the update feed and the browser windows.
    public static bool AllowInsecureSsl() =>
        bool.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL"), out bool allow) && allow;
}
