using System.Security.Cryptography;
using System.Text;
using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// How the webhook endpoint responds to what Stripe actually sends it.
///
/// These exist because the distinction they cover was originally wrong and only showed up
/// against real Stripe: every payment also produces charge.succeeded and charge.updated, and
/// rejecting those made Stripe retry them with backoff for days and the endpoint look broken.
/// </summary>
[TestClass]
public class StripeWebhookParsingTests
{
    private const string WebhookSecret = "whsec_test_secret_for_signing_only";

    private static StripeOrderPaymentService GetService()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:SecretKey"] = "sk_test_notused",
                ["Stripe:PublishableKey"] = "pk_test_notused",
                ["Stripe:WebhookSecret"] = WebhookSecret
            })
            .Build();

        return new StripeOrderPaymentService(configuration, new Mock<ILogger<StripeOrderPaymentService>>().Object);
    }

    /// <summary>Builds the Stripe-Signature header the same way Stripe does, so the payload verifies.</summary>
    private static string Sign(string payload)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        byte[] hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(WebhookSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));

        return $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string EventPayload(string type, string objectType, string id) =>
        "{\"id\":\"evt_test\",\"object\":\"event\",\"type\":\"" + type + "\",\"api_version\":\"2024-06-20\","
        + "\"data\":{\"object\":{\"id\":\"" + id + "\",\"object\":\"" + objectType + "\"}}}";

    [TestMethod]
    public void ParseWebhook_MarksASucceededPaymentAsHandled()
    {
        string payload = EventPayload("payment_intent.succeeded", "payment_intent", "pi_123");

        Result<OrderPaymentWebhookResult> result = GetService().ParseWebhook(payload, Sign(payload));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(result.Data!.Handled);
        Assert.IsTrue(result.Data.Succeeded);
        Assert.AreEqual("pi_123", result.Data.PaymentIntentId);
    }

    [TestMethod]
    public void ParseWebhook_MarksAFailedPaymentAsHandled()
    {
        string payload = EventPayload("payment_intent.payment_failed", "payment_intent", "pi_456");

        Result<OrderPaymentWebhookResult> result = GetService().ParseWebhook(payload, Sign(payload));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(result.Data!.Handled);
        Assert.IsTrue(result.Data.Failed);
    }

    /// <summary>
    /// The regression this file exists for. A charge event is legitimate and correctly signed;
    /// it just is not ours. It must succeed-but-unhandled so the endpoint answers 200, rather
    /// than failing and inviting days of Stripe retries.
    /// </summary>
    [TestMethod]
    public void ParseWebhook_AcknowledgesAnEventItDoesNotActOn()
    {
        string payload = EventPayload("charge.succeeded", "charge", "ch_789");

        Result<OrderPaymentWebhookResult> result = GetService().ParseWebhook(payload, Sign(payload));

        Assert.IsTrue(result.Success, "A charge event must not be reported as a failure.");
        Assert.IsFalse(result.Data!.Handled);
        Assert.IsFalse(result.Data.Succeeded);
        Assert.IsFalse(result.Data.Failed);
    }

    /// <summary>
    /// A mis-signed webhook is someone claiming an order was paid for, so this is the one case
    /// that must fail.
    /// </summary>
    [TestMethod]
    public void ParseWebhook_RejectsAForgedSignature()
    {
        string payload = EventPayload("payment_intent.succeeded", "payment_intent", "pi_forged");

        Result<OrderPaymentWebhookResult> result = GetService()
            .ParseWebhook(payload, $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},v1=deadbeef");

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void ParseWebhook_RejectsAMissingSignature()
    {
        string payload = EventPayload("payment_intent.succeeded", "payment_intent", "pi_unsigned");

        Result<OrderPaymentWebhookResult> result = GetService().ParseWebhook(payload, string.Empty);

        Assert.IsFalse(result.Success);
    }
}
