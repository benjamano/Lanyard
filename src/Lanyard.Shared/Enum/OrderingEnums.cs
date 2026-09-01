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
    Cancelled = 5
}

/// <summary>
/// How a kitchen order has been paid for.
///
/// v1 only ever produces <see cref="Unpaid"/> and <see cref="PaidAtTill"/> - nothing in the
/// app charges a card. This exists now, rather than being added later, so that introducing
/// online payment is a matter of adding members and a pre-confirm step rather than a
/// migration that has to rewrite the payment state of orders already in the table.
/// </summary>
public enum KitchenOrderPaymentStatus
{
    Unknown = 0,

    /// <summary>Order placed; the customer pays staff when they collect it.</summary>
    Unpaid = 1,

    /// <summary>Staff marked the order as settled at the till.</summary>
    PaidAtTill = 2
}
