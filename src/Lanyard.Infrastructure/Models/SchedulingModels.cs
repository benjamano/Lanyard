namespace Lanyard.Infrastructure.Models
{
    // Company-scoped catalog of job positions (Customer Service Advisor, Supervisor, Manager...).
    // Deliberately separate from Identity roles: roles are permission flags ("can manage files"),
    // a position is an HR fact about what someone does on shift. Contract requirements and
    // time-off allowances are tiered company -> position -> user, and a rota shift can be tagged
    // with the position being worked. Company-scoped (not location-scoped) for the same reason
    // as StaffDocumentType - it's company policy, and staff move between locations.
    public class StaffPosition
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        // Index into BrandConstants.ChartCategoricalLight/Dark (mod length) - the rota colours
        // shifts by position, and reusing the validated chart palette keeps every position
        // distinguishable in both themes without inventing new hex values per company.
        public int ColorIndex { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }

    // A user may hold several positions (a supervisor who also covers the front desk); exactly
    // one is primary whenever any are held. The primary position is what the tiered
    // contract/allowance resolution looks at, so "which tier applies to this person" always has
    // one answer.
    public class UserPosition
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required Guid StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public bool IsPrimary { get; set; }

        public DateTime CreateDate { get; set; }
    }

    // The minimum/maximum a person's contract expects of their weekly rota - e.g. play2day's
    // "at least one shift of two or more hours per week" is MinShiftsPerWeek = 1 with
    // MinShiftLengthHours = 2. One table holds all three tiers, keyed by which of
    // StaffPositionId / UserId is set:
    //   - both null              -> the company-wide default (one row per company)
    //   - StaffPositionId set    -> override for everyone whose primary position this is
    //   - UserId set             -> override for one person
    // A null field on an override row means "inherit from the tier below", so a position can
    // override just MaxHoursPerWeek and still pick up the company's MinShiftsPerWeek. Three
    // partial unique indexes in ApplicationDbContext enforce one row per tier key (the same
    // NULL-is-distinct workaround CompanyOnboardingSettings needs).
    public class ContractRequirement
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public Guid? StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public string? UserId { get; set; }
        public UserProfile? User { get; set; }

        public int? MinShiftsPerWeek { get; set; }
        public decimal? MinShiftLengthHours { get; set; }
        public decimal? MinHoursPerWeek { get; set; }
        public decimal? MaxHoursPerWeek { get; set; }

        public DateTime UpdateDate { get; set; }
        public string? UpdatedByUserId { get; set; }

        public bool IsOverrideTier => StaffPositionId is not null || UserId is not null;

        public bool HasAnyValue =>
            MinShiftsPerWeek is not null || MinShiftLengthHours is not null
            || MinHoursPerWeek is not null || MaxHoursPerWeek is not null;
    }

    // The PIN a staff member types on the clock-in terminal. The terminal identifies the person
    // by name first (they tap themselves on today's roster), so the PIN only has to verify that
    // one user - it never has to be unique or looked up, which is why a salted Identity
    // PasswordHasher hash is the right shape here rather than a searchable digest. Its own
    // table rather than a UserProfile column so GDPR erasure is a row delete and a user without
    // a PIN is simply absent, not a nullable column to remember to check.
    public class UserClockInPin
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required string PinHash { get; set; }

        public DateTime SetDate { get; set; }

        // Null when the user set it themselves from their account page; otherwise the manager
        // who set it from the user editor.
        public string? SetByUserId { get; set; }
    }

    // One row per company, created lazily on first save (same lifecycle as
    // CompanyOnboardingSettings). Holds the knobs the scheduling module needs that are policy
    // rather than data: when the holiday year rolls over, how a "day" of leave converts to
    // hours, and how far ahead of a shift the reminder email goes out.
    public class CompanySchedulingSettings
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        // UK tax year default (6 April) - time-off allowances reset on this date.
        public int FinancialYearStartMonth { get; set; } = 4;
        public int FinancialYearStartDay { get; set; } = 6;

        // Allowances are stored in hours so a half-day or a three-hour absence deducts exactly;
        // this is only the display/entry conversion ("30 days" = 30 * HoursPerDay).
        public decimal HoursPerDay { get; set; } = 8;

        public int ShiftReminderLeadHours { get; set; } = 24;

        public DateTime UpdateDate { get; set; }
    }
}
