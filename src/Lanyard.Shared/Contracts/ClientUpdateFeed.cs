namespace Lanyard.Shared.Contracts;

// What GET api/client-updates/{clientId}/feed returns to a kiosk.
public class ClientUpdateFeedResponse
{
    // The version the server wants this kiosk on, or null when there's nothing to move to (no
    // known release, or a pin to a release that was withdrawn) - the kiosk then stays put.
    public string? TargetVersion { get; set; }

    // A Velopack releases feed ({"Assets":[...]}) holding only the target's full package, taken
    // unchanged from that release's own releases.win.json. Left as raw JSON so the server doesn't
    // need Velopack's types and no field Velopack relies on (SHA1, SHA256, Size...) can be lost.
    public string FeedJson { get; set; } = EmptyFeedJson;

    // Where to download each asset in FeedJson from, keyed by its FileName. Velopack also uses
    // FileName as the local package path, so it has to stay a bare file name rather than a URL.
    public Dictionary<string, string> DownloadUrls { get; set; } = [];

    public const string EmptyFeedJson = "{\"Assets\":[]}";
}

public static class ClientUpdateRoutes
{
    public const string FeedTemplate = "api/client-updates/{clientId:guid}/feed";

    public static string Feed(Guid clientId) => $"api/client-updates/{clientId}/feed";
}

// Hub methods the server invokes on a kiosk. Both sides use these constants so a typo can't
// silently drop the call.
public static class ClientHubEvents
{
    // Server -> kiosk, client result: bool, true when an update was found and is being applied.
    public const string CheckForUpdates = "CheckForUpdates";
}
