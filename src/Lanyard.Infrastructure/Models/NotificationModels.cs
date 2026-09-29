using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // One browser or installed app that has agreed to receive push notifications for a person.
    // The endpoint is a URL at the browser vendor's push service (Google, Apple, Mozilla); P256dh
    // and Auth are the keys the payload is encrypted with, so the vendor can't read it.
    // An endpoint belongs to one browser profile, so it is unique: when someone else signs in on
    // that phone and the app re-syncs, the row moves to them rather than being duplicated.
    // Removed on sign-out from that device, when the push service says it's gone, after repeated
    // failures, or after 90 days without the app being opened there (docs/DATA_RETENTION.md).
    public class UserPushSubscription
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required string Endpoint { get; set; }
        public required string P256dh { get; set; }
        public required string Auth { get; set; }

        // "Safari on iPhone", "Chrome on Android" - what the person sees in their device list.
        public required string DeviceLabel { get; set; }

        public DateTime CreatedUtc { get; set; }

        // Refreshed whenever the app is opened on that device, which is what the 90-day cleanup reads.
        public DateTime LastSeenUtc { get; set; }

        public DateTime? LastSuccessUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    // A person's choice for one topic. No row means the topic's default (NotificationTopics),
    // so adding a topic later needs no data migration.
    public class NotificationPreference
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public NotificationTopic Topic { get; set; }

        public bool Push { get; set; }
        public bool Email { get; set; }
    }

    // A device where the person is using Lanyard as an installed app (Home Screen on iPhone,
    // installed from Chrome on Android, or a desktop install). Lets a manager see who will actually
    // get a call-off alert. DeviceId is a random id the browser keeps in its own storage; the
    // installed iPhone app has storage separate from Safari, so it reports as its own device.
    public class AppInstallation
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required string DeviceId { get; set; }
        public required string DeviceLabel { get; set; }
        public DevicePlatform Platform { get; set; }

        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}
