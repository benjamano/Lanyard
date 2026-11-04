using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // Feedback about Lanyard itself, for Lanyard's operators (PlatformAdmins) to read. Deliberately
    // not ICompanyOwned: the company tenant filter would hide every other company's feedback from
    // a platform admin, who signs in to one company like anyone else. CompanyId is kept only so the
    // inbox can say which company it came from.
    public class UserFeedback
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public int? CompanyId { get; set; }
        public int? LocationId { get; set; }

        public FeedbackKind Kind { get; set; }

        // +1/-1 for Thumbs, 1-5 for StarRating.
        public int Score { get; set; }

        public string? Comment { get; set; }

        // The page they were on when they sent it, path and query only.
        public required string PagePath { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? ReviewedUtc { get; set; }
        public string? ReviewedByUserId { get; set; }
    }
}
