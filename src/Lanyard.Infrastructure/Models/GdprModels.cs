namespace Lanyard.Infrastructure.Models
{
    // Durable proof that a right-to-erasure request was carried out, kept after the AspNetUsers
    // row itself is gone. ErasedUserId/PerformedByUserId are plain strings, not FKs - the erased
    // user no longer exists by the time this row is queried, and the performing admin must remain
    // attributed here even if their own account is later erased too.
    public class UserErasureRecord
    {
        public Guid Id { get; set; }
        public required string ErasedUserId { get; set; }
        public required string ErasedEmailHash { get; set; }
        public DateTime ErasedAtUtc { get; set; }
        public required string PerformedByUserId { get; set; }
        public string? PerformedByUserName { get; set; }
    }
}
