using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A date or dates a manager has marked on a location's rota: its local school holidays, a bank
    // holiday, or a day the venue is closed. Entered by hand, never looked up, because school
    // holidays differ from one area to the next. Auto-build reads them (RotaHolidayRules) to decide
    // which shift templates run each day. Removed by switching IsActive off.
    public class RotaHoliday : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string Name { get; set; }

        public RotaHolidayKind Kind { get; set; }

        // Inclusive. One day has StartDate == EndDate.
        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }

        // Bank holidays only: whose templates the day uses. Null means Sunday.
        public DayOfWeek? StaffLike { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }

        public bool Covers(DateOnly day) => IsActive && day >= StartDate && day <= EndDate;

        public DayOfWeek StaffedLike => StaffLike ?? DayOfWeek.Sunday;

        public int Days => EndDate.DayNumber - StartDate.DayNumber + 1;
    }

    // What one day is on a location's rota once its holidays are taken into account.
    //   - Closed wins over everything: no template shifts.
    //   - A bank holiday uses the templates of StaffedLike instead of its own weekday.
    //   - School holiday is true inside a school holiday and on a bank holiday (schools are shut).
    public sealed record RotaHolidayDay(
        DateOnly Day,
        RotaHoliday? Closed,
        RotaHoliday? BankHoliday,
        RotaHoliday? SchoolHoliday)
    {
        public bool IsClosed => Closed is not null;

        public bool IsSchoolHoliday => SchoolHoliday is not null || BankHoliday is not null;

        public bool IsOrdinary => Closed is null && BankHoliday is null && SchoolHoliday is null;

        public DayOfWeek StaffedLike => BankHoliday?.StaffedLike ?? Day.DayOfWeek;

        // The one that decides the day, for a label: closed, then bank holiday, then school holiday.
        public RotaHoliday? Main => Closed ?? BankHoliday ?? SchoolHoliday;
    }

    public static class RotaHolidayRules
    {
        public const int MaxNameLength = 80;
        public const int MaxDays = 366;

        public static RotaHolidayDay For(IEnumerable<RotaHoliday> holidays, DateOnly day)
        {
            List<RotaHoliday> covering = holidays.Where(x => x.Covers(day)).OrderBy(x => x.StartDate).ThenBy(x => x.Name).ToList();

            return new RotaHolidayDay(
                day,
                covering.FirstOrDefault(x => x.Kind == RotaHolidayKind.Closed),
                covering.FirstOrDefault(x => x.Kind == RotaHolidayKind.BankHoliday),
                covering.FirstOrDefault(x => x.Kind == RotaHolidayKind.SchoolHoliday));
        }

        // "Staffed like a Sunday", "No shifts", "Holiday templates run".
        public static string Effect(RotaHoliday holiday) => holiday.Kind switch
        {
            RotaHolidayKind.Closed => "Closed: no shifts",
            RotaHolidayKind.BankHoliday => $"Staffed like a {DayName(holiday.StaffedLike)}",
            _ => "School holiday templates run"
        };

        // Short wording for a rota day header.
        public static string ShortLabel(RotaHolidayDay day) => day switch
        {
            { Closed: not null } => "Closed",
            { BankHoliday: RotaHoliday bank } => $"Bank hol · like {DayName(bank.StaffedLike)[..3]}",
            { SchoolHoliday: not null } => "School hols",
            _ => string.Empty
        };

        public static string? Describe(RotaHolidayDay day) => day.Main is RotaHoliday main
            ? $"{main.Name}: {Effect(main)}" + (main.Kind == RotaHolidayKind.BankHoliday && day.SchoolHoliday is not null ? $" (also {day.SchoolHoliday.Name})" : string.Empty)
            : null;

        public static string? Validate(RotaHoliday holiday)
        {
            if (string.IsNullOrWhiteSpace(holiday.Name))
            {
                return "Give it a name, such as Summer holidays or Boxing Day.";
            }

            if (holiday.Name.Trim().Length > MaxNameLength)
            {
                return $"Keep the name to {MaxNameLength} characters or fewer.";
            }

            if (!System.Enum.IsDefined(holiday.Kind))
            {
                return "Choose what kind of day it is.";
            }

            if (holiday.EndDate < holiday.StartDate)
            {
                return "The last day must be on or after the first day.";
            }

            if (holiday.Days > MaxDays)
            {
                return "A holiday can't be longer than a year.";
            }

            if (holiday.StaffLike is DayOfWeek like && !System.Enum.IsDefined(like))
            {
                return "Choose a day of the week to staff it like.";
            }

            return null;
        }

        private static string DayName(DayOfWeek day) =>
            System.Globalization.CultureInfo.GetCultureInfo("en-GB").DateTimeFormat.GetDayName(day);
    }
}
