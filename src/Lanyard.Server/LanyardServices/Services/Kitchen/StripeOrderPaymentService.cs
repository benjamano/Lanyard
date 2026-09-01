using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Lanyard.Application.Services.Kitchen;

public class StripeOrderPaymentService : IOrderPaymentService
{
    private readonly ILogger<StripeOrderPaymentService> _logger;
    private readonly string? _secretKey;
    private readonly string? _publishableKey;
    private readonly string? _webhookSecret;

    /// <summary>
    /// Identifies the order on the Stripe side. Read back off the PaymentIntent so a webhook can
    /// be traced to an order even if the local row is somehow missing.
    /// </summary>
    private const string OrderTokenMetadataKey = "lanyard_order_token";

    public StripeOrderPaymentService(IConfiguration configuration, ILogger<StripeOrderPaymentService> logger)
    {
        _logger = logger;
        _secretKey = configuration["Stripe:SecretKey"];
        _publishableKey = configuration["Stripe:PublishableKey"];
        _webhookSecret = configuration["Stripe:WebhookSecret"];

        if (IsConfigured)
        {
            StripeConfiguration.ApiKey = _secretKey;
        }
        else
        {
            logger.LogWarning(
                "Stripe is not configured (Stripe:SecretKey / Stripe:PublishableKey). Online ordering "
                + "will refuse to take payments until these are set.");
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secretKey) && !string.IsNullOrWhiteSpace(_publishableKey);

    public async Task<Result<OrderPaymentIntent>> CreatePaymentIntentAsync(
        string stripeAccountId,
        int amountCents,
        Guid orderToken,
        string tableLabel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsConfigured)
            {
                return Result<OrderPaymentIntent>.Fail("Card payments are not set up. Please order at the till.");
            }

            if (string.IsNullOrWhiteSpace(stripeAccountId))
            {
                return Result<OrderPaymentIntent>.Fail("This venue is not set up to take payments yet.");
            }

            if (amountCents <= 0)
            {
                return Result<OrderPaymentIntent>.Fail("Order total must be greater than zero.");
            }

            PaymentIntentCreateOptions options = new()
            {
                Amount = amountCents,
                Currency = "gbp",
                // Wallets and cards, decided by Stripe from what the customer's device supports -
                // Apple Pay and Google Pay matter a lot for someone ordering one-handed at a table.
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true },
                Description = $"Food order - {tableLabel}",
                Metadata = new Dictionary<string, string> { [OrderTokenMetadataKey] = orderToken.ToString() }
            };

            // Direct charge on the venue's own account: the money never touches a Lanyard
            // balance, so Lanyard is not holding another company's takings.
            RequestOptions requestOptions = new() { StripeAccount = stripeAccountId };

            PaymentIntent intent = await new PaymentIntentService()
                .CreateAsync(options, requestOptions, cancellationToken);

            return Result<OrderPaymentIntent>.Ok(
                new OrderPaymentIntent(intent.Id, intent.ClientSecret, _publishableKey!, stripeAccountId));
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe refused to create a payment for order {OrderToken}", orderToken);

            return Result<OrderPaymentIntent>.Fail("We couldn't start your payment. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create a payment for order {OrderToken}", orderToken);

            return Result<OrderPaymentIntent>.Fail("We couldn't start your payment. Please try again.");
        }
    }

    public async Task<Result<bool>> IsPaymentSucceededAsync(
        string stripeAccountId,
        string paymentIntentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsConfigured)
            {
                return Result<bool>.Fail("Card payments are not set up.");
            }

            PaymentIntent intent = await new PaymentIntentService().GetAsync(
                paymentIntentId,
                options: null,
                new RequestOptions { StripeAccount = stripeAccountId },
                cancellationToken);

            return Result<bool>.Ok(intent.Status == "succeeded");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read payment {PaymentIntentId}", paymentIntentId);

            return Result<bool>.Fail("Couldn't check the payment status.");
        }
    }

    public async Task<Result<bool>> RefundAsync(
        string stripeAccountId,
        string paymentIntentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsConfigured)
            {
                return Result<bool>.Fail("Card payments are not set up.");
            }

            await new RefundService().CreateAsync(
                new RefundCreateOptions { PaymentIntent = paymentIntentId },
                new RequestOptions { StripeAccount = stripeAccountId },
                cancellationToken);

            _logger.LogInformation("Refunded payment {PaymentIntentId} on account {StripeAccountId}",
                paymentIntentId, stripeAccountId);

            return Result<bool>.Ok(true);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe refused to refund payment {PaymentIntentId}", paymentIntentId);

            return Result<bool>.Fail($"Stripe refused the refund: {ex.StripeError?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refund payment {PaymentIntentId}", paymentIntentId);

            return Result<bool>.Fail("Couldn't issue the refund.");
        }
    }

    public Result<OrderPaymentWebhookResult> ParseWebhook(string payload, string signatureHeader)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_webhookSecret))
            {
                // Refused rather than trusted. An unverified webhook is an unauthenticated
                // caller asserting that an order has been paid for, which is exactly the thing
                // this endpoint must never take on faith.
                return Result<OrderPaymentWebhookResult>.Fail("Stripe:WebhookSecret is not configured.");
            }

            Event stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _webhookSecret);

            if (stripeEvent.Data.Object is not PaymentIntent intent)
            {
                return Result<OrderPaymentWebhookResult>.Fail("Webhook did not carry a payment.");
            }

            bool succeeded = stripeEvent.Type == "payment_intent.succeeded";
            bool failed = stripeEvent.Type is "payment_intent.payment_failed" or "payment_intent.canceled";

            return Result<OrderPaymentWebhookResult>.Ok(
                new OrderPaymentWebhookResult(intent.Id, succeeded, failed));
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Rejected a Stripe webhook with an invalid signature");

            return Result<OrderPaymentWebhookResult>.Fail("Invalid webhook signature.");
        }
    }
}
