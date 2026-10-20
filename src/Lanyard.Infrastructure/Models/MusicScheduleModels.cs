using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// A location's weekly music plan: which playlist plays, and how loud, at each time of day. It
/// applies to every kiosk whose <see cref="Client.LocationId"/> is this location. One per location,
/// created the first time someone saves it - a location with no row has no schedule.
/// </summary>
public class MusicSchedule
{
    public Guid Id { get; set; }

    public required int LocationId { get; set; }
    public Location? Location { get; set; }

    // Off by default, so a half-built schedule never starts changing the music.
    public bool IsEnabled { get; set; }

    public DateTime UpdateDate { get; set; }
    public string? UpdatedByUserId { get; set; }

    public List<MusicDaypart> Dayparts { get; set; } = [];
}

/// <summary>
/// One block of a <see cref="MusicSchedule"/>, e.g. "Quiet mix, weekdays 08:00-10:00 at 40%".
/// Times are UK wall-clock time (see RotaTime). An end at or before the start runs past midnight,
/// and <see cref="DaysOfWeek"/> are the days it starts on: Fri 22:00-02:00 covers early Saturday.
/// Blocks can't overlap; a gap between them means the schedule leaves the music alone.
/// </summary>
public class MusicDaypart
{
    public const int NameMaxLength = 100;

    public Guid Id { get; set; }

    public Guid MusicScheduleId { get; set; }
    public MusicSchedule? MusicSchedule { get; set; }

    [MaxLength(NameMaxLength)]
    public required string Name { get; set; }

    // Same CSV format as AutomationRule.ScheduledDaysOfWeek (see AutomationScheduleDays), but
    // never blank here - the editor always stores the days explicitly.
    [MaxLength(20)]
    public required string DaysOfWeek { get; set; }

    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    // Null means "music off": the kiosks are paused for this block.
    public Guid? PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    // 0-100. Null keeps whatever volume the kiosk is already at.
    public int? Volume { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreateDate { get; set; }
    public string? CreateByUserId { get; set; }
    public DateTime UpdateDate { get; set; }
    public string? UpdatedByUserId { get; set; }

    public bool EndsNextDay => EndTime <= StartTime;
}
