using Lanyard.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

// One published kiosk software release (a LanyardClient-x.y.z GitHub release), mirrored from
// GitHub so the server can decide which version each kiosk runs. Deliberately not ICompanyOwned:
// a release is shared by every company, so promoting or withdrawing one is a PlatformAdmin action.
[Index(nameof(Version), IsUnique = true)]
public class ClientRelease
{
    public int Id { get; set; }

    [MaxLength(ClientVersionFormat.MaxLength)]
    public string Version { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Tag { get; set; } = string.Empty;

    // GitHub's prerelease flag at the time it was published. A normal release counts as stable
    // straight away; a prerelease only reaches canaries until it's promoted.
    public bool IsPrerelease { get; set; }

    public DateTime PublishedAt { get; set; }

    public DateTime? PromotedToStableAt { get; set; }

    [MaxLength(450)]
    public string? PromotedByUserId { get; set; }

    // Set when a release turns out to be bad. A withdrawn release is never a target, so kiosks on
    // it move back to the newest release still allowed for their ring.
    public DateTime? WithdrawnAt { get; set; }

    [MaxLength(450)]
    public string? WithdrawnByUserId { get; set; }

    [MaxLength(500)]
    public string? WithdrawnReason { get; set; }

    public DateTime CreateDate { get; set; }
}
