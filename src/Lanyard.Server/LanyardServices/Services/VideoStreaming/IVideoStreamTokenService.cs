namespace Lanyard.Application.Services.VideoStreaming;

/// <summary>
/// Issues and validates short-lived capability tokens that gate the two anonymous video
/// streaming entry points. A token can only be obtained via a command or call that travels
/// over a client's SignalR hub connection, so an attacker who merely knows the (unguessable)
/// kiosk or publisher URL cannot activate a camera or hijack a publisher without one.
/// </summary>
public interface IVideoStreamTokenService
{
    /// <summary>Issued when the server tells a client to launch its publisher; the publisher
    /// page must present it to register as the video source for that client.</summary>
    string IssuePublisherToken(Guid clientId);

    bool ValidatePublisherToken(Guid clientId, string? token);

    /// <summary>Issued to a client over the hub when it launches a kiosk; the kiosk must
    /// present it to open a remote video session (validation slides the expiry so a
    /// long-running kiosk stays authorised).</summary>
    string IssueViewerToken(Guid clientId);

    bool ValidateViewerToken(Guid clientId, string? token);
}
