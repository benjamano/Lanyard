namespace Lanyard.Shared.Enum;

/// <summary>
/// Lifecycle of a customer's food order, from the kitchen's point of view.
/// Ordinary progression is Received -> Preparing -> Ready -> Completed; Cancelled is
/// reachable from any of the first three.
///
/// Lives here rather than in Lanyard.Infrastructure because it crosses a process boundary:
/// the public site (Lanyard.Reach.Web) renders these values but must not take a dependency
/// on the server's EF model to do it.
/// </summary>
public enum KitchenOrderStatus
{
    Unknown = 0,
    Received = 1,
    Preparing = 2,
    Ready = 3,
    Completed = 4,
    Cancelled = 5,

    /// <summary>
    /// Created, but the customer has not finished paying. Deliberately never shown on the
    /// kitchen display: an order in this state may never be paid for, and cooking it would mean
    /// giving away food on the strength of someone opening a checkout page.
    ///
    /// Numbered after the others because the value is persisted; the tracker orders states
    /// explicitly rather than by comparing enum values.
    /// </summary>
    AwaitingPayment = 6
}

/// <summary>
/// How a kitchen order has been paid for.
///
/// Customers now pay at the point of ordering, so <see cref="Unpaid"/> and
/// <see cref="PaidAtTill"/> are no longer produced by any code path - they are retained only
/// so orders taken before that change keep reading correctly. This is why the enum was worth
/// having from the start: the switch to online payment added members instead of forcing a
/// migration to rewrite the payment state of orders already in the table.
/// </summary>
public enum KitchenOrderPaymentStatus
{
    Unknown = 0,

    /// <summary>Historical only: order placed before online payment, settled at the till.</summary>
    Unpaid = 1,

    /// <summary>Historical only: staff marked the order settled at the till.</summary>
    PaidAtTill = 2,

    /// <summary>Checkout started; waiting for the payment provider to confirm.</summary>
    Pending = 3,

    /// <summary>Payment confirmed. Only now does the order reach the kitchen.</summary>
    Paid = 4,

    /// <summary>Payment was refunded, in full, after the order was cancelled.</summary>
    Refunded = 5,

    /// <summary>The customer's payment did not go through; nothing was cooked and nothing was charged.</summary>
    Failed = 6
}
