using Lanyard.Infrastructure.Models;

public class CompanyTenantMember
{
    public int Id { get; set; }

    public int CompanyTenantId { get; set; }
    public CompanyTenant? CompanyTenant { get; set; }

    public required string UserId { get; set; }
    public UserProfile? User { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime UpdateDate { get; set; }
}