using System.Net;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Lanyard.Application.Services.Notifications;

// Sends Web Push messages: encrypts the payload for each device and POSTs it to that device's
// push service, signed with our VAPID key. What the push service answers decides what happens to
// the stored subscription:
//   201           delivered; failures reset
//   404 / 410     the browser unsubscribed or the app was removed; delete it
//   400/401/403   our request was refused: usually our VAPID setup (subject, keys, clock), so the
//                 subscription is kept and an error logged - deleting here would let one bad
//                 deploy wipe every device. A subscription made with old keys is replaced by
//                 lanyardPush.sync the next time the app opens there, or ages out after 90 days.
//   413           payload too big; retried once with a shortened body
//   429           rate limited; the library waits for Retry-After and retries
//   5xx / network retried with back-off, then counted as a failure
// Five failures in a row and the subscription is deleted.
public class WebPushSender(
    IDbContextFactory<ApplicationDbContext> factory,
    IHttpClientFactory httpClientFactory,
    VapidKeys vapidKeys,
    TimeProvider timeProvider,
    ILogger<WebPushSender> logger) : IPushSender
{
    public const string HttpClientName = "WebPush";
    public const int MaxConsecutiveFailures = 5;

    // Push services cap payloads near 4 KB after encryption; stay well under.
    private const int MaxBodyLength = 600;
    private const int TrimmedBodyLength = 120;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly VapidKeys _vapidKeys = vapidKeys;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<WebPushSender> _logger = logger;

    // Waits between retries of a 5xx or network failure. Settable so tests don't sleep.
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    public bool IsConfigured => _vapidKeys.IsConfigured;

    public string? PublicKey => _vapidKeys.PublicKey;

    public async Task<PushSendSummary> SendToUserAsync(string userId, PushContent content, string? onlyEndpoint = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new PushSendSummary(0, 0, 0);
        }

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<UserPushSubscription> subscriptions = await ctx.PushSubscriptions
                .TagWithCallSite()
                .Where(x => x.UserId == userId && (onlyEndpoint == null || x.Endpoint == onlyEndpoint))
                .ToListAsync(cancellationToken);

            if (subscriptions.Count == 0)
            {
                return new PushSendSummary(0, 0, 0);
            }

            PushServiceClient client = new(_httpClientFactory.CreateClient(HttpClientName))
            {
                DefaultAuthentication = new VapidAuthentication(_vapidKeys.PublicKey!, _vapidKeys.PrivateKey!) { Subject = _vapidKeys.Subject },
                AutoRetryAfter = true,
                MaxRetriesAfter = 2
            };

            int delivered = 0;
            int removed = 0;
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            // One device at a time: a person has a handful at most, and one dead device must never
            // stop the others.
            foreach (UserPushSubscription subscription in subscriptions)
            {
                SendOutcome outcome = await SendOneAsync(client, subscription, content, cancellationToken);

                switch (outcome)
                {
                    case SendOutcome.Delivered:
                        subscription.LastSuccessUtc = now;
                        subscription.ConsecutiveFailures = 0;
                        delivered++;
                        break;

                    case SendOutcome.Gone:
                        ctx.PushSubscriptions.Remove(subscription);
                        removed++;
                        break;

                    case SendOutcome.Rejected:
                        break;

                    default:
                        subscription.ConsecutiveFailures++;

                        if (subscription.ConsecutiveFailures >= MaxConsecutiveFailures)
                        {
                            _logger.LogWarning("Removing push device {DeviceLabel} for {UserId} after {Failures} failures in a row",
                                subscription.DeviceLabel, userId, subscription.ConsecutiveFailures);

                            ctx.PushSubscriptions.Remove(subscription);
                            removed++;
                        }

                        break;
                }
            }

            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Another send to this person (say a test while a rota push goes out) removed or
                // changed one of these rows first. The pushes still went; only the bookkeeping is lost.
                _logger.LogWarning("Push bookkeeping for {UserId} clashed with another send: {Error}", userId, ex.Message);
            }

            return new PushSendSummary(subscriptions.Count, delivered, removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notifications to {UserId}", userId);

            return new PushSendSummary(0, 0, 0);
        }
    }

    private enum SendOutcome
    {
        Delivered,
        Gone,
        Rejected,
        Failed
    }

    private async Task<SendOutcome> SendOneAsync(PushServiceClient client, UserPushSubscription subscription, PushContent content, CancellationToken cancellationToken)
    {
        WebPushSubscription target = new() { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

        bool trimmed = false;
        int attempt = 0;

        while (true)
        {
            try
            {
                await client.RequestPushMessageDeliveryAsync(target, BuildMessage(content, trimmed), cancellationToken);

                return SendOutcome.Delivered;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _logger.LogInformation("Push device {DeviceLabel} for {UserId} has gone ({Status}); removing it",
                    subscription.DeviceLabel, subscription.UserId, (int)ex.StatusCode);

                return SendOutcome.Gone;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                // Either this subscription was made with other VAPID keys, or our own setup is wrong
                // (Push__Subject, keys, server clock). The two can't be told apart from here, and the
                // second would hit every device at once, so nothing is deleted.
                _logger.LogError("Push service rejected our request for {DeviceLabel} ({UserId}) with {Status}: {Body}. Check the Push VAPID keys and subject",
                    subscription.DeviceLabel, subscription.UserId, (int)ex.StatusCode, ex.Body);

                return SendOutcome.Rejected;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge && !trimmed)
            {
                trimmed = true;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < RetryDelays.Count && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], _timeProvider, cancellationToken);
                attempt++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Couldn't push to {DeviceLabel} for {UserId}: {Error}", subscription.DeviceLabel, subscription.UserId, ex.Message);

                return SendOutcome.Failed;
            }
        }
    }

    private static bool IsRetryable(Exception ex) => ex switch
    {
        PushServiceClientException push => (int)push.StatusCode >= 500 || push.StatusCode == HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        TaskCanceledException => true, // HttpClient timeout
        _ => false
    };

    private static PushMessage BuildMessage(PushContent content, bool trimmed)
    {
        int limit = trimmed ? TrimmedBodyLength : MaxBodyLength;
        string body = content.Body.Length > limit ? content.Body[..(limit - 1)] + "…" : content.Body;

        string json = JsonSerializer.Serialize(new { title = content.Title, body, url = content.Url, tag = content.Tag }, JsonOptions);

        return new PushMessage(json)
        {
            Urgency = content.Urgency,
            TimeToLive = (int)content.TimeToLive.TotalSeconds,
            Topic = TopicHeader(content.Tag)
        };
    }

    // The Topic header lets the push service replace an undelivered older message with this one.
    // It allows only URL-safe base64 characters, at most 32 of them. A longer tag is hashed rather
    // than cut short, so two different tags never collapse into one topic.
    public static string? TopicHeader(string tag)
    {
        string cleaned = new(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());

        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length <= 32 && cleaned.Length == tag.Length)
        {
            return cleaned;
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));

        return Convert.ToBase64String(hash, 0, 24).Replace('+', '-').Replace('/', '_');
    }
}
