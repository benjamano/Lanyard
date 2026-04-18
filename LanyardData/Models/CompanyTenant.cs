public class CompanyTenant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    
    public DateTime CreateDate { get; set; }
    public DateTime UpdateDate { get; set; }

    public virtual ICollection<CompanyTenantMember> Members { get; set; } = [];
}