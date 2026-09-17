namespace Lanyard.Infrastructure.Models
{
    // Company-scoped catalog of document types staff can be asked to provide (P45, DBS check,
    // First Aid certificate, etc). Company-scoped rather than Location-scoped because this is
    // HR/company policy, mirroring how branding (Company.ThemeColorHex/LogoFileId) already lives
    // on Company, not Location - see CompanyLocationModels.cs.
    public class StaffDocumentType
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public bool RequiresExpiryDate { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public virtual List<StaffDocumentReminderInterval> ReminderIntervals { get; set; } = [];
    }

    // A child table (not a CSV column) so admins can add/remove thresholds as a list - e.g.
    // 30 and 7 days before expiry - and each fires independently, tracked via StaffDocumentReminderSent.
    public class StaffDocumentReminderInterval
    {
        public Guid Id { get; set; }

        public required Guid StaffDocumentTypeId { get; set; }
        public StaffDocumentType? StaffDocumentType { get; set; }

        public int DaysBeforeExpiry { get; set; }

        public bool IsActive { get; set; } = true;
    }

    // One uploaded document instance for one staff member. A join entity referencing FileMetadata
    // rather than adding columns to it - FileMetadata is a generic table shared by folders, company
    // logos, and video devices, and already has its own IsActive soft-delete semantics that a
    // per-staff-document lifecycle would otherwise collide with.
    public class StaffDocument
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required Guid StaffDocumentTypeId { get; set; }
        public StaffDocumentType? StaffDocumentType { get; set; }

        public required Guid FileMetadataId { get; set; }
        public FileMetadata? FileMetadata { get; set; }

        public DateTime? ExpiryDate { get; set; }

        public DateTime UploadedDate { get; set; }
        public required string UploadedByUserId { get; set; }

        public bool IsActive { get; set; } = true;
    }

    // Idempotency log for the expiry-reminder sweep. One row per (document, interval) so N
    // configured thresholds can each fire independently - unlike CourseAssignment.DueSoonReminderSentDate
    // (a single nullable DateTime flag), a single flag here couldn't represent "30-day reminder sent,
    // 7-day reminder still pending" for the same document.
    //
    // ExpiryDateSnapshot is part of the row (and the uniqueness key) so that re-uploading a renewed
    // document with a new ExpiryDate naturally makes every interval eligible to fire again, without
    // needing to hunt down and delete old rows.
    public class StaffDocumentReminderSent
    {
        public Guid Id { get; set; }

        public required Guid StaffDocumentId { get; set; }
        public StaffDocument? StaffDocument { get; set; }

        public required Guid ReminderIntervalId { get; set; }
        public StaffDocumentReminderInterval? ReminderInterval { get; set; }

        public DateTime ExpiryDateSnapshot { get; set; }

        public DateTime SentDate { get; set; }
    }
}
