namespace Lanyard.Infrastructure.DTO.Policies;

// Where one person stands on one policy at a given moment (PolicyRequirementCalculator.StatusAt).
public enum PolicyStatus
{
    UpToDate = 0,
    Outstanding = 1,
    Overdue = 2
}

// Create (Id null) or edit a policy's settings. LocationId null = the whole company (Admins only).
// PositionIds empty = everyone at the location(s).
public record PolicySaveRequest(
    Guid? Id,
    string Name,
    string? Description,
    int? LocationId,
    IReadOnlyCollection<Guid> PositionIds,
    int AcknowledgeWithinDays,
    int? ReacknowledgeEveryMonths);

public record PolicyVersionInfo(
    Guid Id,
    int VersionNumber,
    string FileName,
    string? ChangeSummary,
    bool RequiresReacknowledgement,
    DateTime PublishedDate,
    string? PublishedByName);

// One card on the manager's Policies page. Counts are of the people currently in the audience.
public record PolicySummary(
    Guid Id,
    string Name,
    string? Description,
    int? LocationId,
    string ScopeName,
    IReadOnlyList<string> PositionNames,
    int CurrentVersionNumber,
    DateTime? CurrentVersionPublishedDate,
    int AudienceCount,
    int UpToDateCount,
    int OutstandingCount,
    int OverdueCount,
    bool CanManage);

public record PolicyDetail(
    Guid Id,
    string Name,
    string? Description,
    int? LocationId,
    string ScopeName,
    IReadOnlyList<Guid> PositionIds,
    IReadOnlyList<string> PositionNames,
    int AcknowledgeWithinDays,
    int? ReacknowledgeEveryMonths,
    int CurrentVersionNumber,
    int RequiredVersionNumber,
    IReadOnlyList<PolicyVersionInfo> Versions,
    bool CanManage);

// One row on a policy's Status tab. A Manager only sees their own location's people.
public record PolicyPersonStatus(
    string UserId,
    string DisplayName,
    PolicyStatus Status,
    int? AcknowledgedVersionNumber,
    DateTime? AcknowledgedAtUtc,
    DateTime? DueDate);

// One policy on a member of staff's My Policies page.
public record MyPolicyItem(
    Guid PolicyId,
    string Name,
    string? Description,
    PolicyStatus Status,
    Guid? CurrentVersionId,
    int CurrentVersionNumber,
    string? CurrentVersionChangeSummary,
    DateTime? CurrentVersionPublishedDate,
    int? AcknowledgedVersionNumber,
    DateTime? AcknowledgedAtUtc,
    DateTime? OutstandingFrom,
    DateTime? DueDate);

// What the server saw when the acknowledgement was made. Read from the HTTP request in
// PolicyController, never supplied by the page.
public record PolicyAcknowledgementEvidence(string? IpAddress, string? UserAgent, string? DeviceLabel);

public record PolicyAcknowledgementResult(Guid AcknowledgementId, int VersionNumber, DateTime AcknowledgedAtUtc, bool AlreadyAcknowledged);

// A past acknowledgement, for the person's own history and the manager's view of one person.
// IP address and device are filled in only for Admins.
public record PolicyAcknowledgementRecord(
    Guid Id,
    Guid PolicyId,
    string PolicyName,
    int VersionNumber,
    DateTime AcknowledgedAtUtc,
    string StatementText,
    string? IpAddress,
    string? DeviceLabel);
