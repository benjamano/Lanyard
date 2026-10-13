namespace Lanyard.Infrastructure.Models;

// A root entity that is either shared by every company (CompanyId null - built-in reference data
// seeded by a migration) or owned by exactly one company. ApplicationDbContext filters reads to
// "shared or mine", stamps CompanyId on insert from the current tenant, and refuses any tenant write
// to a shared row, so one company can never see or change another company's rows, nor edit the
// shared ones. Use ICompanyOwned instead when there's no shared data.
public interface ISharedOrCompanyOwned
{
    int? CompanyId { get; set; }
}
