namespace Lanyard.Infrastructure.DTO.Compliance;

public enum ComplianceSectionKind
{
    OverdueTraining = 1,
    ExpiredDocuments = 2,
    ExpiringDocuments = 3,
    OverduePolicies = 4,
    OutstandingPolicies = 5
}

// What to count. LocationId is only honoured for Admins (null = every location in their company);
// a Manager always gets the location they signed in to. MaxRowsPerSection caps the names returned
// per section (the counts are always complete); null returns every row, for the compliance page.
public record ComplianceQuery(int? LocationId, int ExpiryWindowDays, int? MaxRowsPerSection = null)
{
    public const int DefaultExpiryWindowDays = 30;
    public const int MinExpiryWindowDays = 1;
    public const int MaxExpiryWindowDays = 365;
}

// One outstanding item for one person. Names and dates only: never a file id or download link,
// since the files endpoint is not yet safe to point anyone at.
public record ComplianceRow(string UserId, string DisplayName, string Detail, DateTime Date);

public record ComplianceSection(ComplianceSectionKind Kind, int PeopleCount, int ItemCount, List<ComplianceRow> Rows);

public record ComplianceSummary(
    int? LocationId,
    string LocationName,
    int ExpiryWindowDays,
    ComplianceSection OverdueTraining,
    ComplianceSection ExpiredDocuments,
    ComplianceSection ExpiringDocuments,
    ComplianceSection OverduePolicies,
    ComplianceSection OutstandingPolicies)
{
    public IEnumerable<ComplianceSection> Sections => [OverdueTraining, ExpiredDocuments, ExpiringDocuments, OverduePolicies, OutstandingPolicies];
}
