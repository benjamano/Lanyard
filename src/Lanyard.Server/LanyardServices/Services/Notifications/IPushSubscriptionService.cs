using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Notifications;

// What the browser's PushSubscription.toJSON() gives us.
public record PushSubscriptionInput(string Endpoint, string P256dh, string Auth);

// One device in "Your devices". Endpoint is included so the card can mark "this device".
public record PushDeviceView(Guid Id, string DeviceLabel, string Endpoint, DateTime CreatedUtc, DateTime LastSeenUtc, DateTime? LastSuccessUtc);

public interface IPushSubscriptionService
{
    // Saves this browser's subscription for the person, or moves it to them if someone else
    // signed in on the same browser earlier. Also refreshes LastSeenUtc.
    Task<Result<bool>> SaveAsync(string userId, PushSubscriptionInput input, DeviceReport device);

    Task<Result<List<PushDeviceView>>> GetForUserAsync(string userId);

    // Only ever removes the person's own rows.
    Task<Result<bool>> RemoveAsync(string userId, string endpoint);
    Task<Result<bool>> RemoveByIdAsync(string userId, Guid subscriptionId);

    // Subscriptions whose app hasn't been opened since the cutoff. Returns how many went.
    Task<Result<int>> RemoveStaleAsync(DateTime cutoffUtc);
}
