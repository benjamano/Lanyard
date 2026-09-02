namespace Lanyard.Shared.Enum;

/// <summary>
/// How a venue gets food to the customer once the kitchen has made it. This decides what the
/// customer's phone tells them to do after they pay, so getting it wrong sends people to queue at
/// a counter for food that was always going to be carried to them.
/// </summary>
public enum OrderFulfilmentMode
{
    /// <summary>
    /// The customer watches their order progress and collects it from the counter when it is
    /// ready. The phone shows Received, Being made, then Ready.
    /// </summary>
    CollectAtCounter = 0,

    /// <summary>
    /// Staff bring the food to the table the QR code was scanned at. There is nothing for the
    /// customer to do and nothing to watch for, so the phone says so and stops there rather than
    /// showing a progress tracker that ends in an instruction they should ignore.
    /// </summary>
    TableService = 1
}
