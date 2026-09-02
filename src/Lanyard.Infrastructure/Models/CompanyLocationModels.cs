using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models
{
    public class Company
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public string? ThemeColorHex { get; set; }   // e.g. "#c8102e"; null => falls back to BrandConstants.PrimaryColorHex
        public string? SecondaryColorHex { get; set; }   // optional supporting colour for the public site; null => derived from the theme colour
        public Guid? LogoFileId { get; set; }         // FK -> FileMetadata.Id; null => navbar falls back to text-only wordmark
        public FileMetadata? LogoFile { get; set; }
        public Guid? BackgroundImageFileId { get; set; }   // FK -> FileMetadata.Id; null => no background image on login
        public FileMetadata? BackgroundImageFile { get; set; }

        /// <summary>
        /// Square icon for the browser tab on this company's public ordering site.
        ///
        /// Separate from <see cref="LogoFileId"/> on purpose: a logo is a wide wordmark sized for
        /// a navbar, and squeezing one into a 16px square produces an unreadable smudge. They are
        /// different images for different jobs, so they get different fields.
        /// </summary>
        public Guid? FaviconFileId { get; set; }   // FK -> FileMetadata.Id; null => the Lanyard mark
        public FileMetadata? FaviconFile { get; set; }

        /// <summary>Edited ordering terms, refund policy and privacy policy. Empty means defaults.</summary>
        public virtual List<CompanyLegalDocument> LegalDocuments { get; set; } = [];

        /// <summary>
        /// URL-safe identifier used to reach this company's public site before its real domain
        /// is pointed at us (dev, staging, and the gap between onboarding and DNS propagating).
        /// Production traffic resolves by hostname via <see cref="Domains"/> instead.
        /// </summary>
        public string? Slug { get; set; }

        /// <summary>
        /// This company's Stripe Connect account id. Customer payments are created directly on
        /// it, so takings land with the company that cooked the food rather than passing through
        /// a Lanyard-held balance - which would make Lanyard a money transmitter.
        ///
        /// Null means this company cannot take online orders yet; ordering is refused rather
        /// than silently falling back to charging somebody else's account.
        /// </summary>
        public string? StripeAccountId { get; set; }

        /// <summary>
        /// Legal identity and contact details, shown on the customer-facing ordering terms.
        ///
        /// Held per company rather than baked into the terms text so one template serves every
        /// tenant - the E-Commerce Regulations require a trader selling to consumers online to
        /// identify itself, and that identity differs per company by definition.
        /// </summary>
// Registered name, company number, address, contact details and the collection window
        // used to live here. They existed only to be printed into the legal documents and to
        // prove the venue had identified itself; both jobs now belong to the documents
        // themselves, which staff edit directly.

        public virtual List<Location> Locations { get; set; } = [];
        public virtual List<CompanyDomain> Domains { get; set; } = [];
    }

    /// <summary>
    /// A hostname that serves this company's public-facing site (Lanyard.Reach). A company has
    /// several: apex and www at minimum, often a staging host too, which is why this is its own
    /// table rather than a column on <see cref="Company"/>.
    ///
    /// Onboarding a new customer domain is therefore a row insert plus DNS - no redeploy and no
    /// code change, which is the whole point of resolving tenants at runtime.
    /// </summary>
    /// <summary>
    /// A customer-facing legal document, as edited by the company rather than shipped in markup.
    ///
    /// No row means "use Lanyard's default wording", so a company that never opens the editor
    /// still publishes a complete document and nothing had to be backfilled when this arrived.
    /// The body may contain placeholders like {{RegisteredAddress}}, substituted at render time
    /// so that correcting a company's address does not mean re-editing three documents.
    /// </summary>
    public class CompanyLegalDocument
    {
        public int Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public LegalDocumentType DocumentType { get; set; }

        /// <summary>
        /// Sanitised on save. This is written by staff and rendered unescaped on a public page,
        /// so the stored value is the safe one: a compromised staff account must not be able to
        /// put script on a customer's checkout.
        /// </summary>
        public required string BodyHtml { get; set; }

        /// <summary>
        /// Set when someone has actively confirmed this document is ready to show customers.
        ///
        /// This is the readiness signal the ordering path checks, and it replaced a set of
        /// company fields (registered name, address, contact email) that were only ever used to
        /// prove the same thing. A document is what the customer actually reads, so publishing it
        /// is a truer statement of "we have told customers who we are" than a filled-in textbox
        /// that may or may not appear in the wording.
        /// </summary>
        public bool IsPublished { get; set; }

        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
    }

    public class CompanyDomain
    {
        public int Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        /// <summary>
        /// Stored lowercase and without port or scheme, because that is the only form the
        /// request-time lookup ever compares against. Normalise on write, never on read.
        /// </summary>
        public required string Hostname { get; set; }

        /// <summary>
        /// The canonical host for this company. Used to build absolute URLs that outlive the
        /// request that generated them - printed QR codes above all - so that a code printed
        /// today still resolves if an alias is retired later.
        /// </summary>
        public bool IsPrimary { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }
    }

    public class Location
    {
        public int Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        /// <summary>
        /// Whether this venue takes QR food orders. Per-location rather than per-company because
        /// what a site serves - and whether it has a kitchen at all - differs between venues.
        /// </summary>
        public bool OrderingEnabled { get; set; }

        /// <summary>
        /// The manual switch above is "are we taking orders at all". This is the timetable: a
        /// venue with hours set only accepts orders inside them. No rows means no timetable, and
        /// the venue relies on the switch alone.
        /// </summary>
        public virtual List<LocationOpeningHours> OpeningHours { get; set; } = [];

        /// <summary>
        /// Which time zone this venue's opening hours are read in. Everything else in the app
        /// stores UTC, but "we open at nine" means nine o'clock where the venue is, and it keeps
        /// meaning that when the clocks change.
        /// </summary>
        public string TimeZoneId { get; set; } = "Europe/London";

        /// <summary>How the food reaches the customer, which decides what their phone tells them.</summary>
        public OrderFulfilmentMode FulfilmentMode { get; set; } = OrderFulfilmentMode.CollectAtCounter;

        /// <summary>
        /// The kiosk client that prints this venue's kitchen receipts, if any.
        ///
        /// Pointed at from the venue rather than stamping a location onto Client, because a
        /// kiosk is a machine that may do several jobs and this is the one job that belongs to a
        /// venue. Null means no printing; the kitchen screen is the only ticket.
        /// </summary>
        public Guid? ReceiptPrinterClientId { get; set; }
        public Client? ReceiptPrinterClient { get; set; }

        /// <summary>
        /// Bumped whenever this location's menu changes, including an item being marked
        /// unavailable mid-service. Customers already poll their order status, so echoing this
        /// value back on that poll lets a phone notice a stale menu and refetch, without opening
        /// a second live connection just for availability.
        /// </summary>
        public DateTime MenuVersion { get; set; }

        public virtual List<UserLocationMembership> Memberships { get; set; } = [];

        public string GetDisplayName() => $"{Company?.Name} {Name}".Trim();
    }

    /// <summary>
    /// One opening window for one day of the week at one venue.
    ///
    /// A row per window rather than a single open/close pair per day, so a venue that shuts
    /// between lunch and dinner can say so instead of appearing open all afternoon.
    /// </summary>
    public class LocationOpeningHours
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DayOfWeek DayOfWeek { get; set; }

        /// <summary>Local to the venue's own time zone, not UTC - see <see cref="Location.TimeZoneId"/>.</summary>
        public TimeOnly OpensAt { get; set; }

        /// <summary>
        /// Exclusive: an order placed exactly at closing time is refused. Kitchens stop taking
        /// orders at the advertised time rather than one second after it.
        /// </summary>
        public TimeOnly ClosesAt { get; set; }

        public DateTime CreateDate { get; set; }
    }

    public class UserLocationMembership
    {
        public int Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateTime CreateDate { get; set; }
    }
}
