using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // One row per company per feature an admin has touched. No row means the feature is on, so
    // companies that existed before feature switches keep everything, and a new CompanyFeature
    // member ships switched on until someone turns it off.
    public class CompanyFeatureSetting
    {
        public int Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required CompanyFeature Feature { get; set; }

        public bool IsEnabled { get; set; }

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }
}
