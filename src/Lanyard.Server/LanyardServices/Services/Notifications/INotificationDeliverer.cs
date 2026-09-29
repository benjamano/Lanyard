using Lanyard.Infrastructure.DTO.Notifications;

namespace Lanyard.Application.Services.Notifications;

// Turns one queued notification into what the person actually receives (an email, for now).
// Scoped: the worker creates a scope per job.
public interface INotificationDeliverer
{
    Task DeliverAsync(NotificationJob job, CancellationToken cancellationToken = default);
}
