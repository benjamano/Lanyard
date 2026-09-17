namespace Lanyard.Infrastructure.Models
{
    // Onboarding configuration, scoped either to a whole company (LocationId == null) or to one
    // specific location within it (LocationId set) - an admin can configure one company-wide
    // default and optionally override it per location. Enforced via two partial unique indexes
    // (one row per CompanyId where LocationId is null, one row per CompanyId+LocationId pair
    // where it isn't) rather than a single index, since Postgres treats every NULL as distinct
    // and a plain unique index on (CompanyId, LocationId) would let multiple company-wide rows
    // through. A row is created lazily on first Save for whichever scope is being edited.
    public class CompanyOnboardingSettings
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public int? LocationId { get; set; }
        public Location? Location { get; set; }

        public bool SendWelcomeEmail { get; set; }

        public string? WelcomeEmailSubject { get; set; }

        // Raw Quill output from the admin's rich-text editor - fundamentally untrusted the moment
        // it's read back out of storage. Sanitized at the point it's spliced into the outbound
        // email (EmailService.BuildOnboardingWelcomeHtml), not here and not on save, matching the
        // "sanitize at the point it becomes live markup" precedent in RenderTextAreaWidget.razor.
        public string? WelcomeEmailBodyHtml { get; set; }

        public bool AutoAttachStandingDocuments { get; set; }

        public DateTime UpdateDate { get; set; }
    }

    // The "always attach" set (handbook, posters, starter forms) for a company or location's
    // onboarding email - scoped identically to CompanyOnboardingSettings (LocationId null = the
    // company-wide set). A join entity referencing FileMetadata rather than adding columns to it,
    // same reasoning as StaffDocument - FileMetadata is a generic table shared by folders, logos,
    // and video devices.
    public class CompanyOnboardingStandingAttachment
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public int? LocationId { get; set; }
        public Location? Location { get; set; }

        public required Guid FileMetadataId { get; set; }
        public FileMetadata? FileMetadata { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
