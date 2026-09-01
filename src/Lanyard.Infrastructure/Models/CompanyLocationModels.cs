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
        /// URL-safe identifier used to reach this company's public site before its real domain
        /// is pointed at us (dev, staging, and the gap between onboarding and DNS propagating).
        /// Production traffic resolves by hostname via <see cref="Domains"/> instead.
        /// </summary>
        public string? Slug { get; set; }

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
        /// Bumped whenever this location's menu changes, including an item being marked
        /// unavailable mid-service. Customers already poll their order status, so echoing this
        /// value back on that poll lets a phone notice a stale menu and refetch, without opening
        /// a second live connection just for availability.
        /// </summary>
        public DateTime MenuVersion { get; set; }

        public virtual List<UserLocationMembership> Memberships { get; set; } = [];

        public string GetDisplayName() => $"{Company?.Name} {Name}".Trim();
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
