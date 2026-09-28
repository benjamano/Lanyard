namespace Lanyard.Infrastructure.Models
{
    public static class LocationClaimTypes
    {
        public const string LocationId = "lanyard:location_id";

        // The company the signed-in location belongs to. Drives the tenant filter in
        // ApplicationDbContext; sessions issued before it existed fall back to a lookup of LocationId.
        public const string CompanyId = "lanyard:company_id";
    }
}
