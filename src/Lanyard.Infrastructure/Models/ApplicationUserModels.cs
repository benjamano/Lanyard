using Microsoft.AspNetCore.Identity;
using System.Data;

namespace Lanyard.Infrastructure.Models
{
    public class UserProfile : IdentityUser
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }

        public string? PreferredCulture { get; set; }

        // Deliberately not an EF relationship. Dashboards are soft-deleted (IsActive = false)
        // rather than removed, so a real FK buys nothing here, and an id pointing at a
        // deactivated dashboard is an expected state the home page falls back from - not a
        // referential-integrity error.
        public Guid? DefaultDashboardId { get; set; }

        // Distinguishes "I have deliberately chosen the standard home page" from "I have not
        // chosen anything", which a null DefaultDashboardId on its own cannot express. Without
        // it, an organisation-wide default dashboard would be inescapable.
        public bool UseStandardHomePage { get; set; }

        public DateTime? InvitedDate { get; set; }
        public DateTime? PasswordSetDate { get; set; }

        public string GetName()
        {
            return FirstName + " " + LastName;
        }

        // Emails address people by first name - a username is an internal handle and reads
        // oddly in a sentence ("Hi jdoe,"). Falls back through full name then username so a
        // user with no first name still gets a sensible greeting rather than a blank one.
        public string GetGreetingName()
        {
            if (!string.IsNullOrWhiteSpace(FirstName))
            {
                return FirstName.Trim();
            }

            string fullName = GetName().Trim();

            return !string.IsNullOrWhiteSpace(fullName)
                ? fullName
                : UserName ?? Email ?? "there";
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
