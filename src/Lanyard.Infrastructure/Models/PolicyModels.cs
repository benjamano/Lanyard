using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models
{
    // A document staff must read and acknowledge: a fire policy, a health and safety handbook.
    // Owned by one location, or by the whole company when LocationId is null (Admin-only, like a
    // company-wide course). Who has to read it is worked out live from the location's members,
    // optionally narrowed to some positions (PolicyAudiencePosition), so a new starter or someone
    // who moves location is picked up without anyone re-assigning it.
    public class Policy : ICompanyOwned
    {
        public const int MinAcknowledgeWithinDays = 1;
        public const int MaxAcknowledgeWithinDays = 365;
        public const int DefaultAcknowledgeWithinDays = 14;
        public const int MinReacknowledgeEveryMonths = 1;
        public const int MaxReacknowledgeEveryMonths = 60;

        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public int? LocationId { get; set; }
        public Location? Location { get; set; }

        // How long someone has to acknowledge before it counts as overdue.
        public int AcknowledgeWithinDays { get; set; } = DefaultAcknowledgeWithinDays;

        // Ask everyone to read it again every N months even if nothing changed. Null = never.
        public int? ReacknowledgeEveryMonths { get; set; }

        // 0 until the first version is published.
        public int CurrentVersionNumber { get; set; }

        // The oldest version an acknowledgement still counts for: the newest version published
        // with RequiresReacknowledgement (v1 always is). A typo-fix version leaves it alone.
        public int RequiredVersionNumber { get; set; }

        public DateTime CreatedDate { get; set; }

        // Plain string (no FK), cleared when that person is erased - same as Announcement.
        public string? CreatedByUserId { get; set; }

        public bool IsActive { get; set; } = true;

        public List<PolicyVersion> Versions { get; set; } = [];
        public List<PolicyAudiencePosition> Positions { get; set; } = [];
    }

    // Narrows a policy's audience to people holding one of these positions. No rows = everyone at
    // the location. Configuration, not history, so unticking a position removes the row.
    public class PolicyAudiencePosition
    {
        public Guid Id { get; set; }

        public required Guid PolicyId { get; set; }
        public Policy? Policy { get; set; }

        public required Guid StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }
    }

    // One published PDF. Never edited or deleted once published: an acknowledgement points at
    // exactly the version (and the exact bytes, via FileSha256) that the person agreed to.
    public class PolicyVersion : ICompanyOwned
    {
        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        public required Guid PolicyId { get; set; }
        public Policy? Policy { get; set; }

        public int VersionNumber { get; set; }

        // A restricted file (FileMetadata.IsRestricted): never served by the File Manager endpoints,
        // only by PolicyController after checking the reader is meant to see it.
        public required Guid FileMetadataId { get; set; }
        public FileMetadata? FileMetadata { get; set; }

        [MaxLength(64)]
        public required string FileSha256 { get; set; }

        public required string FileName { get; set; }

        public string? ChangeSummary { get; set; }

        public bool RequiresReacknowledgement { get; set; } = true;

        public DateTime PublishedDate { get; set; }
        public string? PublishedByUserId { get; set; }
    }

    // Evidence that one person acknowledged one version: append-only, never updated by the app.
    // Kept for employment + 6 years (docs/DATA_RETENTION.md), so the user FK is Restrict and both
    // account-delete paths re-point it to the placeholder account first (PolicyRetention). A GDPR
    // erasure also clears the IP address and device.
    public class PolicyAcknowledgement : ICompanyOwned
    {
        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        public required Guid PolicyId { get; set; }
        public Policy? Policy { get; set; }

        public required Guid PolicyVersionId { get; set; }
        public PolicyVersion? PolicyVersion { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public DateTime AcknowledgedAtUtc { get; set; }

        // The exact sentence the person ticked, and the hash of the file they were shown, so the
        // record still says what was agreed to if the wording or the file handling ever changes.
        public required string StatementText { get; set; }

        [MaxLength(64)]
        public required string FileSha256 { get; set; }

        [MaxLength(64)]
        public string? IpAddress { get; set; }

        [MaxLength(512)]
        public string? UserAgent { get; set; }

        [MaxLength(100)]
        public string? DeviceLabel { get; set; }
    }

    // Where one person stands on one policy. A cache over the acknowledgements and versions, kept
    // so Compliance and reminders are one indexed query rather than "latest acknowledgement per
    // person per policy compared with every version". PolicyRequirementCalculator owns the dates.
    // Personal working state, not evidence: it goes with the account.
    public class PolicyAssignment : ICompanyOwned
    {
        public Guid Id { get; set; }
        public int CompanyId { get; set; }

        public required Guid PolicyId { get; set; }
        public Policy? Policy { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        // When they came into the policy's audience (or last came back into it).
        public DateTime AssignedDate { get; set; }

        public int? AcknowledgedVersionNumber { get; set; }
        public DateTime? AcknowledgedAtUtc { get; set; }
        public Guid? LastAcknowledgementId { get; set; }

        // Both null = nothing to do. OutstandingFrom set = it shows as "to read" from then on;
        // past DueDate it is overdue.
        public DateTime? OutstandingFrom { get; set; }
        public DateTime? DueDate { get; set; }

        // Claimed before a reminder is sent, so two overlapping sweeps can't both send one (same
        // approach as StaffDocument.ReminderSentDate).
        [ConcurrencyCheck]
        public DateTime? LastReminderSentDate { get; set; }

        public int ReminderCount { get; set; }

        // False once they leave the audience; their acknowledgement state is kept in case they return.
        public bool IsActive { get; set; } = true;
    }
}
