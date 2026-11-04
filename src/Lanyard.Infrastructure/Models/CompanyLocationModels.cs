namespace Lanyard.Infrastructure.Models
{
    public class Company
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        // The public demo company: hidden from the login company picker, reached via the one-click
        // demo login, and wiped + reseeded every night. Never set this on a real customer.
        public bool IsDemo { get; set; }

        public string? ThemeColorHex { get; set; }   // e.g. "#c8102e"; null => falls back to BrandConstants.PrimaryColorHex
        public Guid? LogoFileId { get; set; }         // FK -> FileMetadata.Id; null => navbar falls back to text-only wordmark
        public FileMetadata? LogoFile { get; set; }
        public Guid? BackgroundImageFileId { get; set; }   // FK -> FileMetadata.Id; null => no background image on login
        public FileMetadata? BackgroundImageFile { get; set; }

        // Set when an Admin or Manager makes two-factor authentication compulsory for everyone at
        // the company; null means it's optional. Doubles as the "required since" date managers see.
        public DateTime? TwoFactorRequiredSince { get; set; }
        public string? TwoFactorRequiredByUserId { get; set; }

        public virtual List<Location> Locations { get; set; } = [];
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

        public virtual List<UserLocationMembership> Memberships { get; set; } = [];

        public virtual List<LocationOpeningHours> OpeningHours { get; set; } = [];

        public string GetDisplayName() => $"{Company?.Name} {Name}".Trim();
    }

    /// <summary>
    /// One opening window on one day of the week. A row per window rather than one open/close pair
    /// per day, so a venue that shuts between lunch and dinner can say so.
    /// </summary>
    /// <remarks>
    /// Same shape as the opening hours on the kitchen ordering branch (PR #113), so the two features
    /// share one table: there, ordering is only allowed inside these windows. Two differences to
    /// reconcile when that branch is revived: windows here may run past midnight (ClosesAt at or
    /// before OpensAt), and removed windows are soft-deleted (IsActive) rather than deleted.
    /// Times are venue-local (RotaTime.VenueZone). No rows for a day means closed that day.
    /// </remarks>
    public class LocationOpeningHours
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DayOfWeek DayOfWeek { get; set; }

        public TimeOnly OpensAt { get; set; }

        /// <summary>
        /// Exclusive. At or before OpensAt means the window closes after midnight, so it runs on into
        /// the next morning (18:00-02:00 on Saturday covers 00:00-02:00 on Sunday).
        /// </summary>
        public TimeOnly ClosesAt { get; set; }

        public bool IsActive { get; set; }

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
