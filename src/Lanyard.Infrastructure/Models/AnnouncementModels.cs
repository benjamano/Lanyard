using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

// Internal staff announcements - the "internal messaging" the README has always claimed the
// server handles. Posted by a manager holding CanPostAnnouncements, read by staff through the
// Announcements dashboard widget.
public class Announcement
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(120, ErrorMessage = "The Title field can not be longer than 120 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2000, ErrorMessage = "The Body field can not be longer than 2000 characters.")]
    public string Body { get; set; } = string.Empty;

    // Pinned announcements sort above everything else, whatever their date.
    public bool IsPinned { get; set; }

    // Nullable to match Course.LocationId - the column has to allow nulls, and an Admin can hold
    // no location context of their own. The add/edit dialog always sets it, so a null should only
    // ever arrive from a direct database edit; such rows are invisible to the widget.
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    // Null means "never expires". Stored UTC.
    public DateTime? ExpiryDate { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    // Deliberately nullable rather than inheriting CreateAndUpdateBase, whose CreateByUserId is a
    // `required string` and so a non-nullable FK that GdprService has to repoint to the placeholder
    // user on erasure. Nullable here keeps announcements out of that machinery entirely.
    public string? CreatedByUserId { get; set; }
    public UserProfile? CreatedByUser { get; set; }
}
