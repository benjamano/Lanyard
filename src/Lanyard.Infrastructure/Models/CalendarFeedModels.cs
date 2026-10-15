namespace Lanyard.Infrastructure.Models
{
    // A private iCal feed of one person's published shifts at one company, for subscribing to from
    // Google, Apple or Outlook calendar. Calendar apps can't sign in, so the feed URL carries a long
    // random token; only a SHA-256 hash of it is stored (as for ClockInTerminal), which means the
    // URL can be shown once and never again - getting it back means regenerating it.
    //
    // At most one active feed per person per company. Regenerating revokes the old row and adds a
    // new one; revoking sets IsActive false, and the old URL then answers 404.
    public class CalendarFeedToken : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required string TokenHash { get; set; }

        // The last few characters of the token, so the person can tell which URL is the live one.
        public required string TokenHint { get; set; }

        public DateTime CreateDate { get; set; }

        // Updated at most hourly - calendar apps poll, and a write per poll would be wasted.
        public DateTime? LastAccessUtc { get; set; }

        public DateTime? RevokedDateUtc { get; set; }
        public string? RevokedByUserId { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
