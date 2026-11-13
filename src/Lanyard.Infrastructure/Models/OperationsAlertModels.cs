using Lanyard.Infrastructure.Enum;
using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// One operational problem, from the moment something notices it until it clears or a person
/// resolves it: a kiosk offline, an automation rule failing, a missed clock-in. Every producer
/// writes through IOperationsAlertWriter, which keeps at most one unresolved alert per
/// <see cref="DedupKey"/> in a company (a unique index on the unresolved rows), so raising the
/// same problem again only refreshes <see cref="LastSeenAtUtc"/> and <see cref="OccurrenceCount"/>.
///
/// <see cref="Title"/> and <see cref="Detail"/> never hold a staff member's name: alerts about a
/// person point at them through <see cref="SubjectUserId"/>, and the name is read through the link,
/// so a GDPR erasure only has to delete those rows.
/// </summary>
public class OperationsAlert : ICompanyOwned
{
    public const int MaxDedupKeyLength = 200;
    public const int MaxTitleLength = 200;
    public const int MaxDetailLength = 1000;
    public const int MaxLinkLength = 300;
    public const int MaxNoteLength = 500;

    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    // Null for a company-wide alert (a kiosk or rule with no location), which only Admins see.
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    public OperationsAlertType Type { get; set; }
    public OperationsAlertSeverity Severity { get; set; }
    public OperationsAlertStatus Status { get; set; }

    [StringLength(MaxDedupKeyLength)]
    public required string DedupKey { get; set; }

    [StringLength(MaxTitleLength)]
    public required string Title { get; set; }

    [StringLength(MaxDetailLength)]
    public string? Detail { get; set; }

    // Where the alert's row links to, for an Admin. Managers who can't open that page get the
    // alerts page instead (see OperationsAlertService).
    [StringLength(MaxLinkLength)]
    public string? LinkUrl { get; set; }

    // What the alert is about. Kiosks and rules aren't linked by foreign key, so the history
    // outlives a deleted kiosk or rule.
    public Guid? SubjectClientId { get; set; }
    public Guid? SubjectRuleId { get; set; }

    public string? SubjectUserId { get; set; }
    public UserProfile? SubjectUser { get; set; }

    public DateTime RaisedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public int OccurrenceCount { get; set; } = 1;

    public string? AcknowledgedByUserId { get; set; }
    public UserProfile? AcknowledgedByUser { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }

    public string? ResolvedByUserId { get; set; }
    public UserProfile? ResolvedByUser { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public OperationsAlertResolution? Resolution { get; set; }

    [StringLength(MaxNoteLength)]
    public string? ResolutionNote { get; set; }
}
