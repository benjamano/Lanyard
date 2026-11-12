using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A shift a location needs every week: "Saturday AM, 09:00 to 14:00, 2 Duty Managers and 4
    // Hosts". Auto-build on the rota turns each template into draft shifts for the dates it covers
    // and picks who works them; nothing here is ever shown to staff. Templates are the rota's
    // recurring demand, which is why they belong to one location rather than the company.
    //
    // Times are venue-local wall-clock (RotaTime), the same convention as StaffAvailability and
    // LocationOpeningHours: an EndTime at or before StartTime runs past midnight into the next day.
    // EffectiveFrom/EffectiveTo bound it to some dates ("summer holidays only"), judged on the day
    // the shift starts. Archived (IsActive = false) rather than deleted, so a template that's
    // already been used can be put back.
    public class ShiftTemplate : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string Name { get; set; }

        public DayOfWeek DayOfWeek { get; set; }
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }

        public int BreakMinutes { get; set; }

        public DateOnly? EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }

        // Term time, school holidays (the location's RotaHoliday dates) or both.
        public TemplateRuns Runs { get; set; } = TemplateRuns.EveryWeek;

        public bool IsActive { get; set; } = true;

        public List<ShiftTemplateSlot> Slots { get; set; } = [];

        public DateTime CreateDate { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }

        public bool RunsPastMidnight => EndTime <= StartTime;

        public TimeSpan Length => RunsPastMidnight
            ? TimeSpan.FromDays(1) - (StartTime - EndTime)
            : EndTime - StartTime;

        public int TotalHeadcount => Slots.Sum(x => x.Headcount);

        public bool AppliesOn(DateOnly day) =>
            IsActive
            && day.DayOfWeek == DayOfWeek
            && (EffectiveFrom is not DateOnly from || day >= from)
            && (EffectiveTo is not DateOnly to || day <= to);

        // Whether the template runs on a day once the location's holidays are counted: never on a
        // closed day; on a bank holiday it runs if it's for the weekday the day is staffed like;
        // Runs decides between term time and school holidays. The dates are still the real day's.
        public bool AppliesOn(RotaHolidayDay day) =>
            IsActive
            && !day.IsClosed
            && day.StaffedLike == DayOfWeek
            && Runs switch
            {
                TemplateRuns.TermTimeOnly => !day.IsSchoolHoliday,
                TemplateRuns.SchoolHolidaysOnly => day.IsSchoolHoliday,
                _ => true
            }
            && (EffectiveFrom is not DateOnly from || day.Day >= from)
            && (EffectiveTo is not DateOnly to || day.Day <= to);
    }

    // How many people of one position a template needs. A template lists each position once;
    // Headcount is how many shifts of that position auto-build makes. Zero is allowed: the shift then
    // only exists for parties and private hires to add people to (an evening shift on a night with
    // no hire needs nobody). Regulars are the people who normally work it - Auto-build gives them
    // the shift first whenever they can work it.
    public class ShiftTemplateSlot
    {
        public Guid Id { get; set; }

        public required Guid ShiftTemplateId { get; set; }
        public ShiftTemplate? ShiftTemplate { get; set; }

        public required Guid StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public int Headcount { get; set; } = 1;

        public List<ShiftTemplateSlotRegular> Regulars { get; set; } = [];
    }

    // Someone who normally works a template's position ("Rupert does the Saturday manager shift").
    // Configuration, replaced with its slot whenever the template is saved; it goes with the
    // person's account.
    public class ShiftTemplateSlotRegular
    {
        public Guid Id { get; set; }

        public required Guid ShiftTemplateSlotId { get; set; }
        public ShiftTemplateSlot? ShiftTemplateSlot { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public int SortOrder { get; set; }
    }

    // The monthly look at a location's published rota (TemplateSuggestionHostedService): how many
    // template or hire staffing changes it suggested. FirstSetup runs are for a location with no
    // templates yet that has been building its rota by hand: the suggestions are its first templates,
    // and MatchPercent is how much of that rota Auto-build with them would have made the same. Shift Templates shows a banner while the latest
    // run has suggestions and nobody has reviewed or dismissed it; the suggestions themselves are
    // worked out again when someone opens them, so they're never stale.
    public class TemplateSuggestionRun : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public DateTime RunUtc { get; set; }

        public DateOnly From { get; set; }
        public DateOnly To { get; set; }

        public int Suggestions { get; set; }

        public bool FirstSetup { get; set; }

        public int? MatchPercent { get; set; }

        public DateTime? DismissedUtc { get; set; }
        public string? DismissedByUserId { get; set; }

        public bool IsOpen => Suggestions > 0 && DismissedUtc is null;
    }
}
