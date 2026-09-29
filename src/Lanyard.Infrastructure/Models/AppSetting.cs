#nullable enable

namespace Lanyard.Infrastructure.Models;

public class AppSetting : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime CreateDate { get; set; }
}
