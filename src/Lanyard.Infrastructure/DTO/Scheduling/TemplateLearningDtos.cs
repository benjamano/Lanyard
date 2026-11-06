using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// Someone who usually worked a learned line, and how many of the days it ran they worked it.
public sealed record LearnedRegular(string UserId, string Name, int Days);

// One position on a learned template. DaysSeen is how many of the weekday's worked days in the
// period it ran on; DaysOpen how many days that weekday was worked at all. HireOnly lines ran
// (almost) only on days with a private hire during them, so they're learned with Headcount 0 -
// the hire's staffing adds the people.
public sealed record LearnedLine(
    Guid StaffPositionId,
    string PositionName,
    int Headcount,
    bool HireOnly,
    List<LearnedRegular> Regulars,
    int DaysSeen,
    int DaysOpen);

public enum LearnedStatus
{
    // No template at this location runs on that day at those times.
    New,

    // A template already says exactly this.
    Same,

    // A template runs then, but needs different numbers or regulars.
    Changed
}

// A template the past rota suggests. Changes says how it differs from ExistingTemplateId's.
public sealed record LearnedTemplate(
    string Name,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    List<LearnedLine> Lines,
    LearnedStatus Status,
    Guid? ExistingTemplateId,
    List<string> Changes)
{
    public string Key => $"{(int)DayOfWeek}|{StartTime:HH\\:mm}|{EndTime:HH\\:mm}";
}

// "A Laser tag + Play hire needs 1 more Manager": the median number of hire-only shifts of that
// position worked during each such hire.
public sealed record LearnedHireStaffing(
    PrivateHireKind HireKind,
    Guid StaffPositionId,
    string PositionName,
    int Count,
    int HiresSeen,
    bool AlreadySet)
{
    public string Key => $"{(int)HireKind}|{StaffPositionId}";
}

// How close Auto-build with the learned templates gets to the rota that was actually worked.
// SamePerson: same day, times, position and person. SameShift: right shift, someone else.
// Missed: shifts it wouldn't have made at all (one-offs, cover, changes); Extra: shifts it would
// have made that nobody worked.
public sealed record LearnedCheck(int ActualShifts, int SamePerson, int SameShift, int Missed, int Extra, List<string> Examples)
{
    public int MatchPercent => ActualShifts == 0 ? 0 : (int)Math.Round(100.0 * SamePerson / ActualShifts);

    public int ShiftPercent => ActualShifts == 0 ? 0 : (int)Math.Round(100.0 * (SamePerson + SameShift) / ActualShifts);
}

// Everything learned from a location's published rota between From and To.
public sealed record TemplateLearningResult(
    DateOnly From,
    DateOnly To,
    int ShiftsRead,
    int LeftOut,
    List<LearnedTemplate> Templates,
    List<LearnedHireStaffing> HireStaffing,
    LearnedCheck? Check)
{
    public int Suggestions => Templates.Count(x => x.Status != LearnedStatus.Same) + HireStaffing.Count(x => !x.AlreadySet);
}
