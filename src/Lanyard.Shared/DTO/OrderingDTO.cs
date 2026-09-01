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
}

/// <summary>Result of scanning a table's printed QR code.</summary>
public class TableResolutionDto
{
    public int CompanyId { get; set; }
    public int LocationId { get; set; }
    public required string LocationName { get; set; }
    public required string TableLabel { get; set; }
    public bool OrderingEnabled { get; set; }
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
}

/// <summary>
/// A company's legal identity, for the customer-facing ordering terms.
/// </summary>
public class TenantLegalDetailsDto
{
    public required string CompanyName { get; set; }
    public string? LegalName { get; set; }
    public string? CompanyNumber { get; set; }
    public string? RegisteredAddress { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public int CollectionHoldMinutes { get; set; }

    /// <summary>
    /// False when the company has not filled in enough to identify itself. The terms page says
    /// so plainly rather than rendering a document full of blanks, which would look like a bug
    /// and satisfy nobody.
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

    public List<OrderStatusLineDto> Lines { get; set; } = [];
}

public class OrderStatusLineDto
{
    public required string Name { get; set; }
    public int Quantity { get; set; }
    public int UnitPriceCents { get; set; }

    /// <summary>
    /// Allergens as declared when the order was placed, snapshotted alongside name and price for
    /// the same reason: what the customer was told is what the ticket must say, even if the menu
    /// is edited afterwards.
    /// </summary>
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
