using System.Threading.Channels;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

// In-memory queue between the features that raise notifications and the worker that sends them.
// Held in memory only: anything still queued when the app stops is lost, which is acceptable for
// "your rota changed" style messages (the same single-instance assumption as the clock-in
// terminal tokens). Bounded so a runaway caller can't eat memory; when full, the oldest job is
// dropped and logged rather than blocking a page.
public class NotificationDispatcher : INotificationDispatcher
{
    public const int Capacity = 5000;

    private readonly Channel<NotificationJob> _channel;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(ILogger<NotificationDispatcher> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<NotificationJob>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped => _logger.LogWarning("Notification queue full; dropped a {Topic} notification for {UserId}", dropped.Topic, dropped.UserId));
    }

    public ChannelReader<NotificationJob> Reader => _channel.Reader;

    public void Enqueue(IEnumerable<string> userIds, NotificationTopic topic, NotificationPayload payload)
    {
        foreach (string userId in userIds.Where(x => !string.IsNullOrEmpty(x) && x != ApplicationDbContext.SystemDeletedUserPlaceholderId).Distinct())
        {
            _channel.Writer.TryWrite(new NotificationJob(userId, topic, payload));
        }
    }
}
