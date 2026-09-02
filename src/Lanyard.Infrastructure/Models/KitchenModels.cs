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

        /// <summary>Allergens this dish contains, declared under the Food Information Regulations.</summary>
        public Allergen ContainsAllergens { get; set; } = Allergen.None;

        /// <summary>
        /// Allergens this dish may contain through cross-contamination. Deliberately a separate
        /// field: "may contain traces of nuts" is a different claim from "contains nuts", and
        /// collapsing the two is how someone gets hurt.
        /// </summary>
        public Allergen MayContainAllergens { get; set; } = Allergen.None;

        /// <summary>
        /// Set only when someone has actively declared this dish's allergens.
        ///
        /// This exists because an empty <see cref="ContainsAllergens"/> is ambiguous - it reads
        /// identically as "contains none of the fourteen" and "nobody has filled this in yet".
        /// Treating blank as allergen-free would turn every half-finished menu item into a false
        /// safety claim, so an unconfirmed item is withheld from the public menu instead.
        /// </summary>
        public bool AllergensConfirmed { get; set; }

        /// <summary>Choices the customer makes when ordering this dish, if any.</summary>
        public virtual List<MenuItemOptionGroup> OptionGroups { get; set; } = [];
    }

    /// <summary>
    /// A set of choices attached to a dish: "Choose your side", "Choose your drink".
    ///
    /// Modelled as groups rather than a flat list of extras because the rules that matter are
    /// per group - a meal deal is "exactly one side" and "exactly one drink", not "any two of
    /// these six things".
    /// </summary>
    public class MenuItemOptionGroup
    {
        public int Id { get; set; }

        public required int MenuItemId { get; set; }
        public MenuItem? MenuItem { get; set; }

        /// <summary>Shown as the question above the choices: "Choose your side".</summary>
        public required string Name { get; set; }

        /// <summary>
        /// How many choices the customer must make. Zero makes the group optional; one or more
        /// makes it required, which is the same thing said once rather than kept in a separate
        /// IsRequired flag that could contradict it.
        /// </summary>
        public int MinSelections { get; set; } = 1;

        /// <summary>
        /// Most groups are "pick one" and render as radio buttons. Anything higher renders as
        /// checkboxes and caps how many can be ticked.
        /// </summary>
        public int MaxSelections { get; set; } = 1;

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public virtual List<MenuItemOption> Options { get; set; } = [];
    }

    /// <summary>
    /// One choice within a group: "Beans", "Peas", "Chips".
    /// </summary>
    public class MenuItemOption
    {
        public int Id { get; set; }

        public required int OptionGroupId { get; set; }
        public MenuItemOptionGroup? OptionGroup { get; set; }

        public required string Name { get; set; }

        /// <summary>
        /// Added to the dish's price when chosen, in pence, and usually zero - swapping peas for
        /// beans costs nothing. Signed, so a cheaper choice can discount the dish rather than
        /// forcing every variant to be priced upward from the cheapest.
        /// </summary>
        public int PriceDeltaCents { get; set; }

        /// <summary>Cleared when the kitchen runs out of just this choice, leaving the dish orderable.</summary>
        public bool IsAvailable { get; set; } = true;

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        /// <summary>
        /// Allergens for this choice specifically. A choice genuinely changes what is in the
        /// meal - beans and a brioche bun do not carry the same allergens - so declaring them
        /// only on the parent dish would either overstate every variant or understate some.
        /// </summary>
        public Allergen ContainsAllergens { get; set; } = Allergen.None;
        public Allergen MayContainAllergens { get; set; } = Allergen.None;

        /// <summary>
        /// Same meaning, and the same reason, as <see cref="MenuItem.AllergensConfirmed"/>: blank
        /// must never be read as "contains nothing". An unconfirmed choice is withheld from
        /// customers rather than offered with an empty declaration.
        /// </summary>
        public bool AllergensConfirmed { get; set; }
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

        /// <summary>
        /// When a kitchen ticket was printed for this order, and the claim that stops a second
        /// one being printed.
        ///
        /// Payment confirmation can genuinely run twice at once - Stripe's webhook and the
        /// customer's own status poll can reconcile the same payment in the same instant - and
        /// the kitchen screen absorbs that because it keys tickets on order id. Paper cannot:
        /// two tickets on the pass means the order gets cooked twice. Claimed with a conditional
        /// update so only one caller can win it.
        /// </summary>
        public DateTime? ReceiptPrintedDate { get; set; }

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

        /// <summary>
        /// The price of one of this line, including any option deltas already added in. Stored
        /// as the effective price rather than the base so that every existing total calculation
        /// (quantity times unit price) stays correct without knowing options exist.
        /// </summary>
        public int UnitPriceCentsSnapshot { get; set; }

        /// <summary>
        /// Allergens as declared at order time. Snapshotted for the same reason as name and
        /// price: a menu correction tomorrow must not rewrite what the customer was told today,
        /// and staff handing the food over need the declaration the customer actually saw.
        /// </summary>
        public Allergen ContainsAllergensSnapshot { get; set; }

        public int Quantity { get; set; }

        public virtual List<KitchenOrderItemOption> Options { get; set; } = [];
    }

    /// <summary>
    /// A choice the customer made on one order line, snapshotted.
    ///
    /// The kitchen reads this off the ticket to know whether it is plating beans or peas, so it
    /// carries its own copy of the names rather than joining back to the menu: renaming a choice
    /// next week must not change what a ticket printed today said.
    /// </summary>
    public class KitchenOrderItemOption
    {
        public int Id { get; set; }

        public required int OrderItemId { get; set; }
        public KitchenOrderItem? OrderItem { get; set; }

        /// <summary>Kept for reporting ("how often is beans chosen"), never for display.</summary>
        public int? MenuItemOptionId { get; set; }
        public MenuItemOption? MenuItemOption { get; set; }

        public required string GroupNameSnapshot { get; set; }
        public required string OptionNameSnapshot { get; set; }

        /// <summary>
        /// The delta as priced at order time. Already included in the line's
        /// <see cref="KitchenOrderItem.UnitPriceCentsSnapshot"/>; kept separately so a receipt can
        /// show why the line costs what it does.
        /// </summary>
        public int PriceDeltaCentsSnapshot { get; set; }

        public Allergen ContainsAllergensSnapshot { get; set; }
    }
}
