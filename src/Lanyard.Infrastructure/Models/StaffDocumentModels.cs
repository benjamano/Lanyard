using System.ComponentModel.DataAnnotations;

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

        // Custom reminder date chosen by the uploader alongside ExpiryDate, rather than a
        // type-level "days before expiry" policy - each document's reminder timing is its own call.
        public DateTime? ReminderDate { get; set; }

        // Idempotency flag for the expiry-reminder sweep. A single nullable DateTime is enough
        // here (unlike a per-interval log) because there's exactly one reminder per document -
        // same shape as CourseAssignment.DueSoonReminderSentDate. Always null on a freshly
        // uploaded document, so re-uploading a renewed document naturally re-arms its reminder.
        //
        // [ConcurrencyCheck] (not a schema change - no new column) makes SaveChangesAsync include
        // the original value in the UPDATE's WHERE clause, so two overlapping sweeps racing to
        // claim the same reminder can't both win: the loser's UPDATE affects zero rows and EF
        // throws DbUpdateConcurrencyException instead of silently overwriting. Works identically
        // against the EF InMemory test provider, unlike ExecuteUpdateAsync (relational-only).
        [ConcurrencyCheck]
        public DateTime? ReminderSentDate { get; set; }

        public DateTime UploadedDate { get; set; }
        public required string UploadedByUserId { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
