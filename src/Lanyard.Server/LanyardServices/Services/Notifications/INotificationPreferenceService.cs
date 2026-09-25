using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Notifications;

public interface INotificationPreferenceService
{
    // Every topic, with the person's choice or the default.
    Task<Result<List<TopicPreference>>> GetForUserAsync(string userId);

    Task<Result<TopicPreference>> SaveAsync(string userId, NotificationTopic topic, bool push, bool email);
}
