using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

internal static class ShiftClaimQueries
{
    // Claims still in progress (ShiftClaim.OpenStatuses), as a query filter.
    public static IQueryable<ShiftClaim> Open(this IQueryable<ShiftClaim> claims) =>
        claims.Where(x => ShiftClaim.OpenStatuses.Contains(x.Status));
}
