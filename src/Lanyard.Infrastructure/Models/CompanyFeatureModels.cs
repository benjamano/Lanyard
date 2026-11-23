using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // One row per location per feature a platform admin has touched. Only a row with IsEnabled
    // means the location has the feature: no row means off, so a new location, or a newly shipped
    // CompanyFeature member, starts with nothing until someone switches it on.
    public class LocationFeatureSetting
    {
        public int Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required CompanyFeature Feature { get; set; }

        public bool IsEnabled { get; set; }

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }
    }
}
