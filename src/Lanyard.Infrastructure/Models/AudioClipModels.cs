using Lanyard.Infrastructure.Enum;
using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// The company-wide defaults for how far, and how quickly, the music dips under a clip. A clip
/// can override each of them.
/// </summary>
public static class AudioClipDefaults
{
    public const int DuckToPercent = 20;
    public const int FadeDownMs = 500;
    public const int FadeUpMs = 1500;
}

/// <summary>
/// A short recording played over the kiosk music: a closing-time call, a birthday jingle, a safety
/// message. Not called an announcement, because Announcement is already the staff notice board.
/// </summary>
public class AudioClip : ICompanyOwned
{
    public const int NameMaxLength = 100;

    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    [Required]
    [StringLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    // The audio itself, kept in the file store like any other upload. Only ever null on a deleted
    // clip whose file has since been deleted from the file manager.
    public Guid? FileMetadataId { get; set; }
    public FileMetadata? FileMetadata { get; set; }

    public double DurationSeconds { get; set; }

    // Null means every location in the company; otherwise only that location's kiosks play it.
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    // 0-100: how loud the clip itself plays.
    public int Volume { get; set; } = 100;

    // Null takes the AudioClipDefaults value.
    public int? DuckToPercent { get; set; }
    public int? FadeDownMs { get; set; }
    public int? FadeUpMs { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime? UpdateDate { get; set; }
    public DateTime? DeleteDate { get; set; }

    // Nullable so a GDPR erasure can clear it (GdprService.AnonymizeAttributionAsync).
    public string? CreatedByUserId { get; set; }
    public UserProfile? CreatedByUser { get; set; }

    public int EffectiveDuckToPercent => DuckToPercent ?? AudioClipDefaults.DuckToPercent;
    public int EffectiveFadeDownMs => FadeDownMs ?? AudioClipDefaults.FadeDownMs;
    public int EffectiveFadeUpMs => FadeUpMs ?? AudioClipDefaults.FadeUpMs;
}

/// <summary>
/// One clip sent to one kiosk, and how it went: who asked, and whether it played.
/// </summary>
public class AudioClipPlay : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    public Guid AudioClipId { get; set; }
    public AudioClip? AudioClip { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public AudioClipPlaySource Source { get; set; }

    // Set when a person pressed Play; nullable so a GDPR erasure can clear it.
    public string? TriggeredByUserId { get; set; }
    public UserProfile? TriggeredByUser { get; set; }

    public AudioClipPlayStatus Status { get; set; }

    [StringLength(500)]
    public string? Error { get; set; }

    public DateTime RequestedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}
