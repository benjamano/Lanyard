using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Takes payment for a customer's food order.
///
/// Charges are created on the venue's own Stripe Connect account, not a Lanyard-held one, so
/// takings go straight to the company that cooked the food. Lanyard never holds customer money,
/// which keeps it out of money-transmission territory as soon as a second company is live.
/// </summary>
public interface IOrderPaymentService
{
    /// <summary>True when Stripe is configured at all; false in a dev environment with no keys.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Starts a payment for an order. Returns the client secret the customer's browser needs to
    /// complete it, plus the connected account id Stripe.js must be initialised with.
    /// </summary>
    Task<Result<OrderPaymentIntent>> CreatePaymentIntentAsync(
        string stripeAccountId,
        int amountCents,
        Guid orderToken,
        string tableLabel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms with Stripe that a PaymentIntent really did succeed.
    /// </summary>
    /// <remarks>
    /// Used to re-check state rather than trusting anything the browser reports. A customer's
    /// phone claiming "payment succeeded" is not evidence; only Stripe's own answer is.
    /// </remarks>
    Task<Result<bool>> IsPaymentSucceededAsync(
        string stripeAccountId,
        string paymentIntentId,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> RefundAsync(
        string stripeAccountId,
        string paymentIntentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a webhook's signature and returns the PaymentIntent id and whether it
    /// succeeded. Returns a failure when the signature does not verify - an unsigned or
    /// mis-signed webhook is an attacker claiming an order was paid for.
    /// </summary>
    Result<OrderPaymentWebhookResult> ParseWebhook(string payload, string signatureHeader);
}

public record OrderPaymentIntent(string PaymentIntentId, string ClientSecret, string PublishableKey, string StripeAccountId);

public record OrderPaymentWebhookResult(string PaymentIntentId, bool Succeeded, bool Failed);
