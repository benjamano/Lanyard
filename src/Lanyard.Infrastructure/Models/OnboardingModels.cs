namespace Lanyard.Infrastructure.Models
{
    // Company-scoped onboarding configuration - effectively 1:1 with Company (enforced via a
    // unique index on CompanyId), mirroring how StaffDocumentType is company-scoped HR policy
    // rather than location-scoped. A row is created lazily on first Save, not alongside Company.
    public class CompanyOnboardingSettings
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

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

    // The "always attach" set (handbook, posters, starter forms) for a company's onboarding email.
    // A join entity referencing FileMetadata rather than adding columns to it, same reasoning as
    // StaffDocument - FileMetadata is a generic table shared by folders, logos, and video devices.
    public class CompanyOnboardingStandingAttachment
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required Guid FileMetadataId { get; set; }
        public FileMetadata? FileMetadata { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
