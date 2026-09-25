using Lanyard.Infrastructure.Enum;

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

        // Company-wide on/off for shift reminder emails. Per-person choices come later with
        // notification settings; until then this is the only switch.
        public bool SendShiftReminders { get; set; } = true;

        // How early before a shift starts (and how late after it ends) the terminal will let
        // someone clock in. Outside it the clock-in is refused - an unscheduled or cover shift is
        // added by a manager on the Timesheets page instead.
        public int ClockInWindowMinutes { get; set; } = 60;

        public DateTime UpdateDate { get; set; }
    }

    // One person working one stretch at one location. There is deliberately no week/month
    // container: play2day plan a month ahead but change individual shifts at short notice, so
    // "published" is a property of each shift, and a later publish only has to tell the people
    // whose shifts actually changed.
    //
    // Publish lifecycle (see GetState):
    //   PublishedDateUtc null                   -> draft, managers only
    //   UpdateDate > PublishedDateUtc           -> edited since publish, re-notify on next publish
    //   !IsActive && RemovalPending             -> removed after publish, not yet told
    //
    // Automated scheduling (future) plugs in by generating Shift rows through the same
    // IRotaService.SaveShiftAsync validation; it would add StaffAvailability (user, weekday, time
    // range) and LocationDemand (location, weekday, position, headcount) tables to read from,
    // not change this one.
    public class Shift
    {
        public Guid Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public Guid? StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        public int BreakMinutes { get; set; }

        public string? Notes { get; set; }

        public DateTime? PublishedDateUtc { get; set; }
        public string? PublishedByUserId { get; set; }

        // Where the shift started when it was last published. A published shift that's since been
        // moved into another week must still be picked up by publishing the week it used to be in
        // (that's the week the person was told about), so publish and the unpublished-change count
        // match on this as well as on StartUtc.
        public DateTime? PublishedStartUtc { get; set; }

        public bool RemovalPending { get; set; }

        // The StartUtc this shift's reminder email was sent for. Compared against StartUtc, so
        // moving the shift re-arms the reminder without anything having to clear it. Claimed
        // before the email is queued with a single conditional UPDATE (RotaService.
        // ClaimShiftReminderAsync) - not a concurrency token, which would add this column to every
        // shift update and make an ordinary rota save fail if it overlapped a claim.
        public DateTime? ReminderSentForStartUtc { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; }
        public required string CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }

        public decimal PaidHours =>
            Math.Max(0m, (decimal)(EndUtc - StartUtc).TotalMinutes - BreakMinutes) / 60m;

        public ShiftState GetState()
        {
            if (!IsActive)
            {
                return RemovalPending ? ShiftState.RemovedPendingNotice : ShiftState.Removed;
            }

            if (PublishedDateUtc is null)
            {
                return ShiftState.Draft;
            }

            return UpdateDate is DateTime updated && updated > PublishedDateUtc
                ? ShiftState.Changed
                : ShiftState.Published;
        }

        public bool NeedsPublishing => GetState() is ShiftState.Draft or ShiftState.Changed or ShiftState.RemovedPendingNotice;
    }

    // A tablet at a venue that staff clock in and out on. The device proves it's this terminal with
    // a long random token held in an HttpOnly cookie set when a manager paired it; only a SHA-256
    // hash of that token is stored, so a database leak doesn't hand out working terminals.
    // Revoking (IsActive = false) takes effect on the terminal's next check-in or clock action.
    public class ClockInTerminal
    {
        public Guid Id { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string Name { get; set; }

        public required string DeviceTokenHash { get; set; }

        public required string CreatedByUserId { get; set; }
        public DateTime CreateDate { get; set; }

        public DateTime? LastSeenUtc { get; set; }

        public DateTime? RevokedDateUtc { get; set; }
        public string? RevokedByUserId { get; set; }

        public bool IsActive { get; set; } = true;
    }

    // One stretch someone was actually at work: clocked in, and (once finished) clocked out. Linked
    // to the published shift it was worked against where there is one, so the Timesheets page can
    // show late starts and missed shifts. At most one open entry (ClockOutUtc null) per person is
    // enforced by a partial unique index, which is what stops two terminals racing to clock the
    // same person in twice.
    //
    // Kept for 6 years like shifts (docs/DATA_RETENTION.md); UserId is a Restrict FK and user
    // deletion re-points it to the placeholder account via ScheduleRetention.
    public class TimeEntry
    {
        public Guid Id { get; set; }

        public required string UserId { get; set; }
        public UserProfile? User { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public Guid? ShiftId { get; set; }
        public Shift? Shift { get; set; }

        public Guid? ClockInTerminalId { get; set; }
        public ClockInTerminal? ClockInTerminal { get; set; }

        public DateTime ClockInUtc { get; set; }
        public DateTime? ClockOutUtc { get; set; }

        public ClockMethod ClockInMethod { get; set; }
        public ClockMethod? ClockOutMethod { get; set; }

        // Set when something about the entry wants a manager's eye before payroll - closed
        // automatically, clocked out at a different site - with the reason in plain words.
        public bool NeedsReview { get; set; }
        public string? ReviewReason { get; set; }

        public string? Notes { get; set; }

        public string? ApprovedByUserId { get; set; }
        public DateTime? ApprovedDateUtc { get; set; }

        public DateTime CreateDate { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }

        public bool IsActive { get; set; } = true;

        public bool IsOpen => ClockOutUtc is null;

        public bool IsApproved => ApprovedDateUtc is not null;

        // Time on the clock, not paid time: no break is deducted here, because whether and how
        // breaks are deducted from clocked hours is a payroll decision, not something to guess.
        public decimal? ClockedHours => ClockOutUtc is DateTime outUtc
            ? Math.Max(0m, (decimal)(outUtc - ClockInUtc).TotalMinutes) / 60m
            : null;
    }
}
