using Microsoft.AspNetCore.Identity;
using System.Data;

namespace LanyardData.Models
{
    public class UserProfile : IdentityUser
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public DateTime? DateOfBirth { get; set; }

        public string GetName()
        {
            return this.FirstName + " " + this.LastName;
        }
    }

    public class ApplicationRole : IdentityRole
    {
        public required string CreatedByUserId { get; set; }
        public virtual UserProfile? CreatedByUser { get; set; }

        public DateTime CreateDate { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
