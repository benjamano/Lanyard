using Lanyard.Shared.Enum;

namespace Lanyard.Shared.DTO;

/// <summary>
/// Everything the public site needs to render itself as a given tenant. Returned by hostname
/// lookup, so a new customer domain becomes a database row rather than a redeploy.
///
/// Note there is no file id anywhere in here: the logo is fetched from a separate endpoint
/// keyed by company, matching how CompanyBrandingController already serves company logos
/// without exposing which FileMetadata row backs them.
/// </summary>
public class TenantBrandingDto
{
    public int CompanyId { get; set; }
    public required string CompanyName { get; set; }

    /// <summary>Canonical hostname, used to build absolute URLs (printed QR codes, canonical tags).</summary>
    public string? PrimaryHost { get; set; }

    public required string ThemeColorHex { get; set; }
    public required string SecondaryColorHex { get; set; }

    /// <summary>
    /// Foreground colour that meets WCAG AA contrast against <see cref="ThemeColorHex"/>.
    /// Computed server-side so a tenant who picks a pale brand colour does not end up with
    /// white-on-yellow buttons nobody can read.
    /// </summary>
    public required string OnPrimaryColorHex { get; set; }

    public bool HasLogo { get; set; }

    /// <summary>
    /// Whether this company has uploaded its own browser-tab icon. Separate from the logo: a
    /// navbar wordmark makes a poor favicon, so the two are chosen independently.
    /// </summary>
    public bool HasFavicon { get; set; }
}

/// <summary>Result of scanning a table's printed QR code.</summary>
public class TableResolutionDto
{
    public int CompanyId { get; set; }
    public int LocationId { get; set; }
    public required string LocationName { get; set; }
    public required string TableLabel { get; set; }
    public bool OrderingEnabled { get; set; }

    /// <summary>
    /// False when the venue is switched off or outside its opening hours. Distinct from
    /// OrderingEnabled, which is only the switch: a venue can be perfectly well set up for QR
    /// ordering and simply shut at four in the afternoon.
    ///
    /// Null means we could not work it out - the availability lookup itself failed. Deliberately
    /// nullable rather than defaulting to false, because "we could not check" and "we are shut"
    /// call for completely different things to be said to a customer, and collapsing the first
    /// into the second tells a queue of people at an open venue that it is closed.
    /// </summary>
    public bool? OrderingOpen { get; set; }

    /// <summary>Why it is closed and when it opens again, written for the customer.</summary>
    public string? ClosedMessage { get; set; }

    /// <summary>
    /// Decides what the phone says after payment: watch for it to be ready and collect it, or
    /// sit tight because someone is bringing it over.
    /// </summary>
    public OrderFulfilmentMode FulfilmentMode { get; set; }
}

public class MenuDto
{
    public int LocationId { get; set; }

    /// <summary>
    /// Echoed back on the order-status poll. A phone that sees a value different from the one
    /// its cached menu carries knows to refetch - this is how an item taken off mid-service
    /// reaches a customer who is already browsing, without a second live connection.
    /// </summary>
    public DateTime MenuVersion { get; set; }

    public List<MenuCategoryDto> Categories { get; set; } = [];
}

public class MenuCategoryDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public List<MenuItemDto> Items { get; set; } = [];
}

public class MenuItemDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int PriceCents { get; set; }
    public bool IsAvailable { get; set; }
    public bool HasImage { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Allergens declared for this dish. Present on the public menu because a distance sale
    /// requires the declaration before the customer buys, not on request afterwards.
    /// </summary>
    public Allergen ContainsAllergens { get; set; }
    public Allergen MayContainAllergens { get; set; }

    /// <summary>
    /// Choices the customer makes before this dish can go in the basket - "chips, nuggets and
    /// beans" versus "chips, nuggets and peas". Empty for a dish that is just itself.
    /// </summary>
    public List<MenuItemOptionGroupDto> OptionGroups { get; set; } = [];
}

public class MenuItemOptionGroupDto
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Zero means the customer may skip this group entirely.</summary>
    public int MinSelections { get; set; }

    /// <summary>One renders as radio buttons; more renders as checkboxes with a cap.</summary>
    public int MaxSelections { get; set; }

    public int SortOrder { get; set; }
    public List<MenuItemOptionDto> Options { get; set; } = [];
}

public class MenuItemOptionDto
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Usually zero. Added to the dish price when chosen.</summary>
    public int PriceDeltaCents { get; set; }

    public bool IsAvailable { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Declared per choice, because a choice changes what is actually in the meal. Shown to the
    /// customer at the point they pick it, not buried in the dish's own declaration.
    /// </summary>
    public Allergen ContainsAllergens { get; set; }
    public Allergen MayContainAllergens { get; set; }
}

/// <summary>
/// Whether a company is ready to sell, from the customer site's point of view.
///
/// Once carried the company's registered name, address and contact details so the ordering terms
/// could be assembled from them. The documents now contain their own wording, so all that is
/// left is the question the ordering flow actually asks: has this company published what a
/// customer must be shown before buying?
/// </summary>
public class TenantLegalDetailsDto
{
    public required string CompanyName { get; set; }

    /// <summary>
    /// False until the ordering terms, refund policy and privacy policy have all been published.
    /// The terms and privacy pages say so plainly rather than showing an unfinished draft, and
    /// ordering is refused entirely.
    /// </summary>
    public bool IsComplete { get; set; }
}

public class CreateOrderRequestDto
{
    public required string TableToken { get; set; }
    public string? CustomerNote { get; set; }
    public List<CreateOrderLineDto> Lines { get; set; } = [];
}

public class CreateOrderLineDto
{
    public int MenuItemId { get; set; }
    public int Quantity { get; set; }

    /// <summary>
    /// Chosen option ids. Re-validated server-side against the item's own groups - that they
    /// belong to this dish, are still available, and satisfy each group's min/max - because the
    /// price and the allergen declaration both depend on them and neither may be taken from the
    /// client's word.
    /// </summary>
    public List<int> SelectedOptionIds { get; set; } = [];
}

public class CreateOrderResultDto
{
    public Guid OrderToken { get; set; }
    public int TotalCents { get; set; }
    public required string TableLabel { get; set; }

    /// <summary>
    /// What the customer's browser needs to complete payment with Stripe.js.
    ///
    /// The client secret only authorises paying this one order and nothing else, which is why
    /// it is safe to hand to the browser - unlike an API key. The account id is needed because
    /// the charge is created on the venue's own connected account.
    /// </summary>
    public string? ClientSecret { get; set; }
    public string? PublishableKey { get; set; }
    public string? StripeAccountId { get; set; }
}

public class OrderStatusDto
{
    public Guid OrderToken { get; set; }
    public KitchenOrderStatus Status { get; set; }
    public KitchenOrderPaymentStatus PaymentStatus { get; set; }
    public required string TableLabel { get; set; }
    public int TotalCents { get; set; }
    public DateTime CreateDate { get; set; }

    /// <summary>Current menu version for this order's location - see <see cref="MenuDto.MenuVersion"/>.</summary>
    public DateTime MenuVersion { get; set; }

    /// <summary>
    /// Carried on the status poll as well as on table resolution, so a phone that resumes an
    /// order after being closed and reopened still renders the right ending.
    /// </summary>
    public OrderFulfilmentMode FulfilmentMode { get; set; }

    public List<OrderStatusLineDto> Lines { get; set; } = [];
}

public class OrderStatusLineDto
{
    public required string Name { get; set; }
    public int Quantity { get; set; }

    /// <summary>Price of one, options included.</summary>
    public int UnitPriceCents { get; set; }

    /// <summary>
    /// The choices made on this line, as the customer saw them and as the kitchen must read
    /// them. Snapshotted names, so a later menu edit cannot rewrite a printed ticket.
    /// </summary>
    public List<OrderLineOptionDto> Options { get; set; } = [];

    /// <summary>
    /// Allergens as declared when the order was placed, snapshotted alongside name and price for
    /// the same reason: what the customer was told is what the ticket must say, even if the menu
    /// is edited afterwards.
    /// </summary>
    public Allergen ContainsAllergens { get; set; }
}

public class OrderLineOptionDto
{
    public required string GroupName { get; set; }
    public required string OptionName { get; set; }
    public int PriceDeltaCents { get; set; }
    public Allergen ContainsAllergens { get; set; }
}

/// <summary>
/// A ticket as the kitchen display sees it. Pushed over SignalR when an order arrives or its
/// status changes, so the display never has to re-query to stay current.
///
/// Carries the internal order id, unlike everything the customer sees: this only ever travels
/// to authenticated staff on a role-gated hub.
/// </summary>
/// <summary>
/// Kitchen performance over a window, on the wire.
///
/// Exists alongside the service's own KitchenStats record so a custom kitchen client can read
/// these figures without referencing the server's service assembly.
/// </summary>
public class KitchenStatsDto
{
    public int LocationId { get; set; }
    public KitchenStatsPeriod Period { get; set; }
    public int ServedCount { get; set; }
    public int CancelledCount { get; set; }
    public int RefundedCount { get; set; }
    public int TakingsCents { get; set; }

    /// <summary>Null when nothing reached ready in the window - which is not the same as zero.</summary>
    public double? AverageSecondsToReady { get; set; }
}

public class KitchenOrderTicketDto
{
    public int OrderId { get; set; }
    public int LocationId { get; set; }
    public required string TableLabel { get; set; }
    public KitchenOrderStatus Status { get; set; }
    public KitchenOrderPaymentStatus PaymentStatus { get; set; }
    public int TotalCents { get; set; }
    public string? CustomerNote { get; set; }
    public DateTime CreateDate { get; set; }
    public List<OrderStatusLineDto> Lines { get; set; } = [];
}

/// <summary>
/// A rendered legal document, ready to display. The HTML is sanitised when it is saved, so what
/// arrives here is already safe to render unescaped; Reach sanitises again on the way out as
/// defence in depth against a row edited outside the app.
/// </summary>
public class LegalDocumentDto
{
    public required string Html { get; set; }
}

/// <summary>
/// Names the SignalR command that carries a <see cref="KitchenReceiptDto"/> to a kiosk.
///
/// One constant referenced from both ends rather than a matching pair of string literals. A
/// mismatch between a server SendAsync and a client connection.On is dropped silently with no
/// error at either end, and this repository has already lost a production feature to exactly that
/// kind of drift once - see ReachApiConstants.
/// </summary>
public static class KitchenPrinting
{
    public const string PrintCommand = "PrintKitchenReceipt";
}

/// <summary>
/// A kitchen receipt, as sent to the on-site kiosk that prints it.
///
/// Everything the ticket needs is on here as plain text. The kiosk does no lookups and holds no
/// database connection, so a printer that is offline or a kiosk that is restarting can never
/// hold up the payment that produced this.
/// </summary>
public class KitchenReceiptDto
{
    public int OrderId { get; set; }
    public int LocationId { get; set; }
    public required string VenueName { get; set; }
    public required string TableLabel { get; set; }

    /// <summary>Placed time in the venue's own time zone, already formatted for printing.</summary>
    public required string PlacedAt { get; set; }

    /// <summary>True when staff carry the food out, which the ticket says so the kitchen knows.</summary>
    public bool IsTableService { get; set; }

    public string? CustomerNote { get; set; }
    public int TotalCents { get; set; }

    public List<KitchenReceiptLineDto> Lines { get; set; } = [];
}

public class KitchenReceiptLineDto
{
    public int Quantity { get; set; }
    public required string Name { get; set; }

    /// <summary>The choices made on this line, already flattened - "Beans", "Extra cheese".</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>Allergens as declared at order time, already named. Empty when there are none.</summary>
    public List<string> Allergens { get; set; } = [];
}
