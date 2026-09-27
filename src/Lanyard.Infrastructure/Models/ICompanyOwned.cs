namespace Lanyard.Infrastructure.Models;

// A root entity that belongs to exactly one company. ApplicationDbContext applies a global query
// filter to every implementer (so a signed-in user only ever sees their own company's rows) and
// stamps CompanyId on insert from the current tenant, so services don't have to set it by hand.
// Child rows (steps, widgets, members...) don't implement this - they're filtered through their
// parent's navigation instead.
public interface ICompanyOwned
{
    int CompanyId { get; set; }
}
