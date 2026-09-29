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
