using System.Globalization;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// The pure rules behind location checklists: which days one runs, when it's due, whether a day's
/// run is done or missed, and what a valid checklist or task looks like. Shared by the services,
/// the pages and the missed-checklist sweep so they can't disagree.
/// </summary>
public static class ChecklistRules
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 500;
    public const int MaxTaskTitleLength = 120;
    public const int MaxTaskDetailLength = 500;
    public const int MaxUnitLength = 10;
    public const int MaxNoteLength = 500;
    public const int MaxTasks = 100;

    // Twelve hours either side of opening or closing is more than any real routine needs.
    public const int MaxOffsetMinutes = 12 * 60;

    private static readonly CultureInfo Uk = new("en-GB");

    // Monday first, as the rota reads.
    public static IReadOnlyList<DayOfWeek> WeekOrder { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static ChecklistDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ChecklistDays.Monday,
        DayOfWeek.Tuesday => ChecklistDays.Tuesday,
        DayOfWeek.Wednesday => ChecklistDays.Wednesday,
        DayOfWeek.Thursday => ChecklistDays.Thursday,
        DayOfWeek.Friday => ChecklistDays.Friday,
        DayOfWeek.Saturday => ChecklistDays.Saturday,
        _ => ChecklistDays.Sunday
    };

    public static bool RunsOn(ChecklistDays days, DayOfWeek day) => (days & DayFlag(day)) != 0;

    /// <summary>
    /// Whether the checklist runs on a venue day: an active checklist, on one of its days, not on a
    /// day the location is closed (a Closed rota holiday), and - when OnlyWhenOpen - on a day with
    /// opening hours. A location with no opening hours at all counts as open every day.
    /// </summary>
    public static bool AppliesOn(Checklist checklist, DateOnly day, IEnumerable<LocationOpeningHours> hours, RotaHolidayDay? holiday = null)
    {
        if (!checklist.IsActive || !RunsOn(checklist.Days, day.DayOfWeek) || holiday?.IsClosed == true)
        {
            return false;
        }

        if (!checklist.OnlyWhenOpen)
        {
            return true;
        }

        List<LocationOpeningHours> valid = Valid(hours);

        return valid.Count == 0 || valid.Any(x => x.DayOfWeek == day.DayOfWeek);
    }

    /// <summary>
    /// When the day's run is due, in UTC, or null when it has no due time - including an
    /// opening/closing rule on a day with no opening hours. "Before closing" uses the day's last
    /// closing time, which for a window running past midnight is the next morning.
    /// </summary>
    public static DateTime? DueUtc(Checklist checklist, DateOnly day, IEnumerable<LocationOpeningHours> hours)
    {
        switch (checklist.DueRule)
        {
            case ChecklistDueRule.FixedTime:
                return checklist.DueTime is TimeOnly time ? RotaTime.ToUtc(day, time) : null;

            case ChecklistDueRule.AfterOpening:
            {
                List<LocationOpeningHours> today = Valid(hours).Where(x => x.DayOfWeek == day.DayOfWeek).ToList();

                if (today.Count == 0)
                {
                    return null;
                }

                return RotaTime.ToUtc(day, today.Min(x => x.OpensAt)).AddMinutes(checklist.DueOffsetMinutes);
            }

            case ChecklistDueRule.BeforeClosing:
            {
                List<LocationOpeningHours> today = Valid(hours).Where(x => x.DayOfWeek == day.DayOfWeek).ToList();

                if (today.Count == 0)
                {
                    return null;
                }

                DateTime lastClose = today
                    .Select(x => RotaTime.ToUtc(x.ClosesAt <= x.OpensAt ? day.AddDays(1) : day, x.ClosesAt))
                    .Max();

                return lastClose.AddMinutes(-checklist.DueOffsetMinutes);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// A day's run from its ticks: complete once every task is ticked, missed once it's past due
    /// (or, with no due time, once the venue day is over) with tasks left, otherwise not started or
    /// in progress. A run with no tasks has nothing to miss.
    /// </summary>
    public static ChecklistRunStatus Status(int done, int total, DateTime? dueUtc, DateOnly venueDate, DateTime nowUtc)
    {
        if (done >= total)
        {
            return ChecklistRunStatus.Complete;
        }

        bool overdue = dueUtc is DateTime due ? nowUtc > due : RotaTime.Today(nowUtc) > venueDate;

        if (overdue)
        {
            return ChecklistRunStatus.Missed;
        }

        return done == 0 ? ChecklistRunStatus.NotStarted : ChecklistRunStatus.InProgress;
    }

    /// <summary>
    /// Why someone can't tick a checklist's tasks, or null when they can. The same rule for the phone,
    /// the terminal and the manager's board: the location's Managers and Admins always can; anyone else
    /// must be on that day's rota there and, if the checklist is limited to a position, hold it.
    /// </summary>
    public static string? WhyCantTick(bool isManagerAtLocation, bool isOnRota, bool holdsPosition, Guid? positionId, string? positionName, string checklistName)
    {
        if (isManagerAtLocation)
        {
            return null;
        }

        if (!isOnRota)
        {
            return $"Only people on today's rota here can tick {checklistName}.";
        }

        if (positionId is not null && !holdsPosition)
        {
            return $"Only {positionName ?? "the chosen position"} can tick {checklistName}.";
        }

        return null;
    }

    /// <summary>A reading must fit the column: up to 9 digits, 2 after the point.</summary>
    public static bool IsValidReading(decimal reading) => reading > -10_000_000m && reading < 10_000_000m && decimal.Round(reading, 2) == reading;

    /// <summary>
    /// How a reading compares with a task's acceptable range. Both ends are included, and an open
    /// end ("at most 5") is no limit on that side.
    /// </summary>
    public static ChecklistReadingStatus Check(decimal reading, decimal? min, decimal? max)
    {
        if (min is null && max is null)
        {
            return ChecklistReadingStatus.NoRange;
        }

        if (min is decimal low && reading < low)
        {
            return ChecklistReadingStatus.TooLow;
        }

        if (max is decimal high && reading > high)
        {
            return ChecklistReadingStatus.TooHigh;
        }

        return ChecklistReadingStatus.InRange;
    }

    public static bool IsOutOfRange(ChecklistReadingStatus status) => status is ChecklistReadingStatus.TooLow or ChecklistReadingStatus.TooHigh;

    /// <summary>"0–5 °C", "at most 5 °C", "at least 63 °C", or null with no range.</summary>
    public static string? RangeText(decimal? min, decimal? max, string? unit)
    {
        string suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;

        return (min, max) switch
        {
            (decimal low, decimal high) when low == high => $"exactly {ReadingText(low)}{suffix}",
            (decimal low, decimal high) => $"{ReadingText(low)}–{ReadingText(high)}{suffix}",
            (null, decimal high) => $"at most {ReadingText(high)}{suffix}",
            (decimal low, null) => $"at least {ReadingText(low)}{suffix}",
            _ => null
        };
    }

    /// <summary>A reading as people read it: "4", "4.5", "-18.25".</summary>
    public static string ReadingText(decimal reading) => reading.ToString("0.##", Uk);

    /// <summary>"8 °C" - a reading with its unit.</summary>
    public static string ReadingText(decimal reading, string? unit) =>
        string.IsNullOrEmpty(unit) ? ReadingText(reading) : $"{ReadingText(reading)} {unit}";

    /// <summary>What's wrong with a checklist's settings, or null when it can be saved.</summary>
    public static string? Validate(Checklist checklist)
    {
        if (string.IsNullOrWhiteSpace(checklist.Name))
        {
            return "Give the checklist a name.";
        }

        if (checklist.Name.Trim().Length > MaxNameLength)
        {
            return $"The name can be at most {MaxNameLength} characters.";
        }

        if (checklist.Description?.Trim().Length > MaxDescriptionLength)
        {
            return $"The description can be at most {MaxDescriptionLength} characters.";
        }

        if ((checklist.Days & ChecklistDays.EveryDay) == ChecklistDays.None)
        {
            return "Choose at least one day.";
        }

        switch (checklist.DueRule)
        {
            case ChecklistDueRule.AfterOpening:
            case ChecklistDueRule.BeforeClosing:
                if (checklist.DueOffsetMinutes < 0 || checklist.DueOffsetMinutes > MaxOffsetMinutes)
                {
                    return $"The minutes must be between 0 and {MaxOffsetMinutes}.";
                }
                break;

            case ChecklistDueRule.FixedTime:
                if (checklist.DueTime is null)
                {
                    return "Choose the time it's due by.";
                }
                break;

            case ChecklistDueRule.None:
                break;

            default:
                return "Choose when it's due.";
        }

        return null;
    }

    /// <summary>What's wrong with a task, or null when it can be saved.</summary>
    public static string? Validate(ChecklistTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            return "Give the task a title.";
        }

        if (task.Title.Trim().Length > MaxTaskTitleLength)
        {
            return $"The title can be at most {MaxTaskTitleLength} characters.";
        }

        if (task.Detail?.Trim().Length > MaxTaskDetailLength)
        {
            return $"The details can be at most {MaxTaskDetailLength} characters.";
        }

        if (!System.Enum.IsDefined(task.Response))
        {
            return "Choose what ticking it asks for.";
        }

        if (task.Response == ChecklistResponse.Number && task.Unit?.Trim().Length > MaxUnitLength)
        {
            return $"The unit can be at most {MaxUnitLength} characters.";
        }

        if (task.Response != ChecklistResponse.Number && (task.MinReading is not null || task.MaxReading is not null))
        {
            return "Only a number reading can have an acceptable range.";
        }

        if (task.MinReading is decimal min && !IsValidReading(min) || task.MaxReading is decimal max && !IsValidReading(max))
        {
            return "Each end of the range must be a number with at most two decimal places.";
        }

        if (task.MinReading is decimal low && task.MaxReading is decimal high && low > high)
        {
            return "The lowest acceptable reading can't be above the highest.";
        }

        if (!System.Enum.IsDefined(task.ReadingAlert))
        {
            return "Choose what happens when a reading is outside the range.";
        }

        return null;
    }

    /// <summary>"Every day", "Weekdays", "Weekends" or "Mon, Wed, Fri".</summary>
    public static string DaysLabel(ChecklistDays days)
    {
        days &= ChecklistDays.EveryDay;

        return days switch
        {
            ChecklistDays.EveryDay => "Every day",
            ChecklistDays.Weekdays => "Weekdays",
            ChecklistDays.Weekends => "Weekends",
            ChecklistDays.None => "No days",
            _ => string.Join(", ", WeekOrder.Where(d => RunsOn(days, d)).Select(d => Uk.DateTimeFormat.GetAbbreviatedDayName(d)))
        };
    }

    /// <summary>"15 min after opening", "At closing", "By 10:00" or "No set time".</summary>
    public static string DueLabel(Checklist checklist) => checklist.DueRule switch
    {
        ChecklistDueRule.AfterOpening => checklist.DueOffsetMinutes == 0 ? "By opening" : $"{Minutes(checklist.DueOffsetMinutes)} after opening",
        ChecklistDueRule.BeforeClosing => checklist.DueOffsetMinutes == 0 ? "By closing" : $"{Minutes(checklist.DueOffsetMinutes)} before closing",
        ChecklistDueRule.FixedTime when checklist.DueTime is TimeOnly time => $"By {time.ToString("HH:mm", Uk)}",
        _ => "No set time"
    };

    // "15 min", "1 hr", "1 hr 30 min".
    public static string Minutes(int minutes)
    {
        int hours = minutes / 60;
        int rest = minutes % 60;

        return (hours, rest) switch
        {
            (0, _) => $"{rest} min",
            (_, 0) => $"{hours} hr",
            _ => $"{hours} hr {rest} min"
        };
    }

    private static List<LocationOpeningHours> Valid(IEnumerable<LocationOpeningHours> hours) =>
        hours.Where(x => x.IsActive && OpeningHoursRules.IsValid(x.OpensAt, x.ClosesAt)).ToList();
}
