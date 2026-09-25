using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

// The one way features tell people things. Callers hand over who and what; delivery happens on a
// background worker (NotificationDeliveryHostedService), so publishing a rota to forty people
// never waits on forty emails. Push notifications plug into the delivery side later without any
// caller changing.
public interface INotificationDispatcher
{
    // Queues one notification per user. Never throws and never blocks the caller.
    void Enqueue(IEnumerable<string> userIds, NotificationTopic topic, NotificationPayload payload);
}
