using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models
{
    /// <summary>
    /// A heading on a venue's menu ("Mains", "Drinks"). Scoped to a Location, not a Company,
    /// because two sites of the same company rarely serve exactly the same food.
    /// </summary>
    public class MenuCategory
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string Name { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public virtual List<MenuItem> Items { get; set; } = [];
    }

    public class MenuItem
    {
        public int Id { get; set; }

        public required int CategoryId { get; set; }
        public MenuCategory? Category { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        /// <summary>
        /// Integer minor units (pence). Never a floating-point type: accumulating a basket total
        /// from doubles produces totals that are a penny out, and a till that disagrees with the
        /// customer's phone is worse than a slightly awkward type.
        /// </summary>
        public int PriceCents { get; set; }

        public Guid? ImageFileId { get; set; }   // FK -> FileMetadata.Id
        public FileMetadata? ImageFile { get; set; }

        /// <summary>
        /// Cleared when the kitchen runs out mid-service ("86'ing" an item). Distinct from
        /// <see cref="IsActive"/>, which means the item is off the menu entirely: an unavailable
        /// item comes back tomorrow, an inactive one does not.
        /// </summary>
        public bool IsAvailable { get; set; } = true;

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
    }

    /// <summary>
    /// The printed QR code on a table. The customer's URL carries <see cref="Token"/> and never
    /// a location or table id, so scanning a code cannot be turned into probing the estate by
    /// incrementing a number - the same "resolve server-side, never expose a raw id" posture
    /// CompanyBrandingController takes for logo files.
    /// </summary>
    public class QrTableToken
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        /// <summary>Human-facing label the kitchen reads off a ticket: "Table 4", "Zone 2 Lobby".</summary>
        public required string Label { get; set; }

        /// <summary>
        /// Opaque, randomly generated, and rotatable: reprinting a table's code invalidates the
        /// old one without disturbing any other table.
        /// </summary>
        public required string Token { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
    }

    public class KitchenOrder
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        /// <summary>
        /// Handed to the customer so they can poll their own order's status. Deliberately not the
        /// primary key: a sequential id in that URL would let anyone read - and count - every
        /// other order in the venue by decrementing it.
        /// </summary>
        public Guid OrderToken { get; set; }

        public int? QrTableTokenId { get; set; }
        public QrTableToken? QrTableToken { get; set; }

        /// <summary>
        /// The table label as it read when the order was placed. Relabelling or retiring a table
        /// later must not rewrite where a historical order was served.
        /// </summary>
        public required string TableLabelSnapshot { get; set; }

        public KitchenOrderStatus Status { get; set; } = KitchenOrderStatus.Received;
        public KitchenOrderPaymentStatus PaymentStatus { get; set; } = KitchenOrderPaymentStatus.Unpaid;

        /// <summary>Sum of the line snapshots, stored rather than recomputed, for the same reason they are snapshotted.</summary>
        public int TotalCents { get; set; }

        public string? CustomerNote { get; set; }

        /// <summary>
        /// Stripe PaymentIntent for this order, on the venue's own connected account. Stored so
        /// the webhook can find the order it belongs to, and so a refund can be issued later
        /// without the customer being present.
        /// </summary>
        public string? PaymentIntentId { get; set; }

        public DateTime? PaidDate { get; set; }
        public DateTime? RefundedDate { get; set; }

        /// <summary>
        /// When the kitchen marked this order ready.
        ///
        /// Its own column because UpdateDate moves on every status change, so it cannot answer
        /// "how long did this take to cook" once the order is later completed. Set once, on the
        /// first transition to Ready, so re-running the transition cannot inflate the timing.
        /// </summary>
        public DateTime? ReadyDate { get; set; }

        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public virtual List<KitchenOrderItem> Items { get; set; } = [];
    }

    public class KitchenOrderItem
    {
        public int Id { get; set; }

        public required int OrderId { get; set; }
        public KitchenOrder? Order { get; set; }

        /// <summary>
        /// Kept for reporting ("how often do we sell the burger"), but deliberately not the source
        /// of the name or price shown on this line - see the snapshot fields below.
        /// </summary>
        public int? MenuItemId { get; set; }
        public MenuItem? MenuItem { get; set; }

        /// <summary>
        /// Name and price as they stood when the order was placed. Without these, repricing the
        /// menu at 7pm would silently restate the total of an order taken at 6pm, and the till
        /// would disagree with the receipt the customer is holding.
        /// </summary>
        public required string MenuItemNameSnapshot { get; set; }
        public int UnitPriceCentsSnapshot { get; set; }

        public int Quantity { get; set; }
    }
}
