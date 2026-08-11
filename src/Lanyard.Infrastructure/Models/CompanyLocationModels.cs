namespace Lanyard.Infrastructure.Models
{
    public class Company
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public virtual List<Location> Locations { get; set; } = [];
    }

    public class Location
    {
        public int Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreateDate { get; set; }
        public DateTime UpdateDate { get; set; }

        public virtual List<UserLocationMembership> Memberships { get; set; } = [];

        public string GetDisplayName() => $"{Company?.Name} {Name}".Trim();
    }

    public class UserLocationMembership
    {
        public int Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateTime CreateDate { get; set; }
    }
}
