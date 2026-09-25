namespace Lanyard.Application.Services.Notifications;

public record PushSendSummary(int Devices, int Delivered, int Removed);

public interface IPushSender
{
    // False when the server has no VAPID keys (production without Push__ settings): push is off
    // and only email goes out.
    bool IsConfigured { get; }

    string? PublicKey { get; }

    // Sends to every device the person has switched on, or only the one endpoint when given (the
    // "send a test" button). Never throws: failures are logged and reflected in the summary.
    Task<PushSendSummary> SendToUserAsync(string userId, PushContent content, string? onlyEndpoint = null, CancellationToken cancellationToken = default);
}
